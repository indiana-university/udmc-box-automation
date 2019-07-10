// Copyright (C) 2018 The Trustees of Indiana University
// SPDX-License-Identifier: BSD-3-Clause    

namespace UDMC

open System
open System.Net
open System.Net.Http

module ROP =

    let bind (f : 'a -> Async<Result<'b, 'error>>) (a : Async<Result<'a, 'error>>)  : Async<Result<'b, 'error>> = async {
        let! r = a
        match r with
        | Ok value -> return! f value
        | Error err -> return (Error err)
    }

    let compose (f : 'a -> Async<Result<'b, 'e>>) (g : 'b -> Async<Result<'c, 'e>>) : 'a -> Async<Result<'c, 'e>> =
        fun x -> bind g (f x)

    let (>>=) a f = bind f a

    let (>=>) f g = compose f g

    let ok obj = obj |> Ok |> async.Return

    let error(status, msg) = Error(status, msg) |> async.Return  

    let tap f x =
        f x // invoke f with the argument x
        ok x // pass x unchanged to the next step in the workflow


module Types =

    [<CLIMutable>]
    type Submission =
      { SubmitterName : string
        SubmitterEmail : string
        ReportUrl : string
        FileUrl : string
        FileName : string
        VendorName: string
        ProductName: string
        DepartmentName: string
        SsspNumber: string
        DataDomains: string }

    type Status = HttpStatusCode


module API = 

    open ROP
    open Types
    
    open Newtonsoft.Json
    open Newtonsoft.Json.Serialization

    /// Authorize an incoming request
    let authorize secret (req:HttpRequestMessage) =
        let header = req.Headers.Authorization
        if not (isNull header)
           && header.Scheme = "Bearer" 
           && header.Parameter = secret
        then ok req
        else error(HttpStatusCode.Unauthorized, "nope!")

    let JsonSettings = 
        JsonSerializerSettings(
            ContractResolver=CamelCasePropertyNamesContractResolver(),
            DefaultValueHandling=DefaultValueHandling.Populate)
    JsonSettings.Converters.Add(Converters.StringEnumConverter())

    let deserialize<'T> str =
        if String.IsNullOrWhiteSpace(str)
        then error (Status.BadRequest, "Expected a request body but received nothing")
        else 
            try JsonConvert.DeserializeObject<'T>(str) |> ok
            with exn -> error (Status.BadRequest, exn.Message)

    /// Attempt to deserialize the request body as an object of the given type.
    let deserializeBody<'T> (req:HttpRequestMessage) = async { 
        let! body = req.Content.ReadAsStringAsync() |> Async.AwaitTask 
        let! result = deserialize<'T> body
        return result
    }

    let serialize obj = JsonConvert.SerializeObject(obj, JsonSettings)


module Box = 

    open ROP
    open Types

    open Box.V2
    open Box.V2.Models
    open Box.V2.Models.Request
    open Box.V2.Config
    open Box.V2.JWTAuth

    let await = Async.AwaitTask

    let getClient config = 
        let auth = 
            config 
            |> Convert.FromBase64String
            |> Text.Encoding.UTF8.GetString
            |> BoxConfig.CreateFromJsonString 
            |> BoxJWTAuth
        auth.AdminToken() |> auth.AdminClient

    let createSharedLink (client:BoxClient) id = async {
        let req = BoxSharedLinkRequest(Access=Nullable(BoxSharedLinkAccessType.collaborators))
        return! (id, req) |> client.FilesManager.CreateSharedLinkAsync |> await
    }

    let createSubmissionFolder (client:BoxClient) (submission:Submission) folderId  = async { 
        let name = sprintf "%s - %s - %s - %s %s" submission.VendorName submission.ProductName submission.DepartmentName submission.SsspNumber (DateTime.Now.ToLongTimeString())
        let parent = BoxRequestEntity(Id=folderId)
        let req = BoxFolderRequest(Name=name, Parent=parent)
        return! req |> client.FoldersManager.CreateAsync |> await
    }

    let createSubmissionFolder' (config, folderId, submission:Submission) = async { 
        let client = config |> getClient
        let name = sprintf "%s - %s - %s - %s %s" submission.VendorName submission.ProductName submission.DepartmentName submission.SsspNumber (DateTime.Now.ToLongTimeString())
        let parent = BoxRequestEntity(Id=folderId)
        let req = BoxFolderRequest(Name=name, Parent=parent)
        return! req |> client.FoldersManager.CreateAsync |> await
    }

    let copyFile (client:BoxClient) (folder:BoxFolder) templateId  = async {
        let parent = BoxRequestEntity(Id=folder.Id)
        let req = BoxFileRequest(Id=templateId, Parent=parent)
        return! req |> client.FilesManager.CopyAsync |> await
    }

    let createTask (client:BoxClient) (submission:Submission)  (folder:BoxFolder) (template:BoxFile) = async {
        let item = BoxRequestEntity(Id=template.Id, Type=Nullable(BoxType.file))
        let message = sprintf """%s submitted a Data Steward request for '%s'. 
        Submission folder:  https://app.box.com/folder/%s
        Approval documentation:  https://app.box.com/file/%s""" submission.SubmitterEmail submission.SubmitterName folder.Id template.Id
        let req = BoxTaskCreateRequest(Item=item, Message=message)
        return! req |> client.TasksManager.CreateTaskAsync |> await
    }

    let assignTask (client:BoxClient) (task:BoxTask) login = async {
        let taskReq = BoxTaskRequest(Id=task.Id)
        let assignReq = BoxAssignmentRequest(Login=login)
        let req = BoxTaskAssignmentRequest(Task=taskReq, AssignTo=assignReq)
        return! req |> client.TasksManager.CreateTaskAssignmentAsync |> await
    }

    let httpClient = (new HttpClient())

    let createFile (client:BoxClient) (folder:BoxFolder) (stream:IO.Stream) name = async {
        use memoryStream = (new IO.MemoryStream())
        do! stream.CopyToAsync(memoryStream) |> Async.AwaitTask
        let parent = BoxRequestEntity(Id=folder.Id)
        let req = BoxFileRequest(Name=name, Parent=parent)
        return! (req, memoryStream) |> client.FilesManager.UploadAsync |> await
    }

    let createSubmissionBookmark (client:BoxClient) (submission:Submission) (folder:BoxFolder) = async {
        let parent = BoxRequestEntity(Id=folder.Id)
        let req = BoxWebLinkRequest(Name="Survey Response", Url=Uri(submission.ReportUrl), Parent=parent)
        return! client.WebLinksManager.CreateWebLinkAsync(req) |> Async.AwaitTask
    }

    let createSupplementFile (client:BoxClient) (submission:Submission) (folder:BoxFolder) = async {
        if isNull submission.FileUrl
        then return None
        else 
            let! stream = httpClient.GetStreamAsync(submission.FileUrl) |> Async.AwaitTask
            use memoryStream = (new IO.MemoryStream())
            do! stream.CopyToAsync(memoryStream) |> Async.AwaitTask
            let parent = BoxRequestEntity(Id=folder.Id)
            let req = BoxFileRequest(Name=submission.FileName, Parent=parent)
            let! resp = (req, memoryStream) |> client.FilesManager.UploadAsync |> Async.AwaitTask
            return Some(resp)
    }


    let doBoxStuff (config, folderId, commentTemplateId, reviewTemplateId) (log:string->unit) (submission:Submission) = async {
        let client = getClient config
        let assignees = ["jhoerr@iu.edu" ]

        log "Creating folder"
        let! folder = createSubmissionFolder client submission folderId 
        log "Creating submission file"
        let! submissionFile = createSubmissionBookmark client submission folder
        log "Creating supplement file"
        let! supplementFile = createSupplementFile client submission folder
        log "Copying content template"
        let! commentTemplate = copyFile client folder commentTemplateId
        log "Copying review template"
        let! reviewTemplate = copyFile client folder reviewTemplateId
        log "Creating task"
        let! task = createTask client submission folder commentTemplate
        log "Assigning task"
        let! assignedTasks = assignees |> Seq.map (assignTask client task) |> Async.Parallel

        return Ok submission
    }

module Functions =    

    open API
    open ROP
    open Types
    open Box
    
    open System.Net.Http.Headers

    open Microsoft.Azure.WebJobs
    open Microsoft.Azure.WebJobs.Extensions.Http
    open Microsoft.Extensions.Configuration
    open Microsoft.Extensions.Logging
    open Newtonsoft.Json

    open DurableFunctions.FSharp

    /// Read in the app setttings from the environment or a local.settings.json file.
    let config = 
        ConfigurationBuilder()
            .AddJsonFile("local.settings.json", optional=true)
            .AddEnvironmentVariables()
            .Build();

    /// Authorize requests against the configured secret.
    let authorize =  config.GetValue("Secret") |> authorize
    let boxConfig = config.GetValue("BoxConfig")
    let folderId = config.GetValue("BoxFolderId")
    let commentTemplateId = config.GetValue("BoxCommentTemplateId")
    let reviewTemplateId = config.GetValue("BoxReviewTemplateId")
    let doBoxStuff =  (boxConfig, folderId, commentTemplateId, reviewTemplateId) |> doBoxStuff

    /// Process an HTTP request pipeline, returning an HTTP response.
    let http pipeline (req:HttpRequestMessage)=
        async {
            let! result = pipeline req
            match result with
            | Ok(resp) -> return req.CreateResponse(HttpStatusCode.OK, resp)
            | Error(code:HttpStatusCode,msg:string) -> return req.CreateErrorResponse(code, msg)
        } |> Async.StartAsTask

    /// Process a task-based pipeline, returning nothing if successful.
    let task pipeline obj =
        async {
            let! result = pipeline obj
            match result with
            | Ok(resp) -> return ()
            | Error(code,msg) -> 
                msg
                |> sprintf "Pipeline failed with error: %A"
                |> Exception
                |> raise
        } |> Async.StartAsTask

    /// A simple ping/pong function for availability checks.        
    [<FunctionName("PingGet")>]
    let ping
        ([<HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")>] req:HttpRequestMessage) =
        req.CreateResponse(HttpStatusCode.OK, "pong!")


    [<FunctionName("EchoPost")>]
    let test
        ([<HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "echo")>] req:HttpRequestMessage,
         logger:ILogger ) =
        let content = req.Content.ReadAsStringAsync() |> Async.AwaitTask |> Async.RunSynchronously
        content |> sprintf "Received post body:\n%s" |> logger.LogInformation
        let resp = req.CreateResponse(HttpStatusCode.OK)
        resp.Content <- (new StringContent(content))
        resp.Content.Headers.ContentType <- MediaTypeHeaderValue "application/json"
        resp.Content.Headers.ContentType.CharSet <- "utf-8"
        resp

    /// A webhook to receive Data Steward request submissions. This function will validate the submission,
    /// enqueue it to be worked, and return a 200 OK if successful.
    [<FunctionName("WebhookGet")>]
    let webhook
        ([<HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhook")>] req:HttpRequestMessage,
         [<Queue("request-submission")>] queue: ICollector<string>,
         logger:ILogger) = 

        let pipeline = 
            authorize
            >=> deserializeBody<Submission>
            >=> tap (serialize >> queue.Add)
            >=> tap (sprintf "Enqueued submission of %A" >> logger.LogInformation)
           
        req |> http pipeline 

    /// A function to receive the submission message from the queue and process it.
    [<FunctionName("QueueWorker")>]
    let queueWorker
        ([<QueueTrigger("request-submission")>] item: string,
         logger: ILogger) = 

         let pipeline =  
            deserialize<Submission>
            >=> tap (sprintf "Dequeued submission of: %A" >> logger.LogInformation)
            >=> (doBoxStuff logger.LogInformation)
            >=> tap (sprintf "Finished doing Box stuff with: %A" >> logger.LogInformation)
         
         item |> task pipeline

    let policy = ExponentialBackOff { MaxNumberOfAttempts = 3
                                      FirstRetryInterval = TimeSpan.FromSeconds 1.
                                      BackoffCoefficient = 2. }

    let sayHello = Activity.define "SayHello" (sprintf "Hello %s!")

    let createSubmissionFolderActivity = Activity.defineAsync "CreateSubmissionFolder" createSubmissionFolder'

    let workflow (submission:Submission) = orchestrator {
               
        let! folder = Activity.callWithRetries policy createSubmissionFolderActivity (boxConfig, folderId, submission)
        let! hello1 = Activity.call sayHello "Tokyo"
        let! hello2 = Activity.call sayHello "Seattle"
        let! hello3 = Activity.call sayHello "London"

        // returns ["Hello Tokyo!", "Hello Seattle!", "Hello London!"]
        return [hello1; hello2; hello3]
    }

    [<FunctionName("SayHello")>]
    let SayHello([<ActivityTrigger>] name) = Activity.run sayHello

    [<FunctionName("TypedSequence")>]
    let Run 
        ([<QueueTrigger("request-submission")>] item: string,
         [<OrchestrationTrigger>] context: DurableOrchestrationContext) =
        let submission = item |> JsonConvert.DeserializeObject<Submission> 
        let workflow = workflow submission
        Orchestrator.run (workflow, context)    

    [<FunctionName("CreateSubmissionFolder")>]
    let CreateSubmissionFolder([<ActivityTrigger>] submission) = 
        createSubmissionFolderActivity.run (boxConfig, folderId, submission)
