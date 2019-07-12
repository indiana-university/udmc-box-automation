module Functions

open API
open ROP
open Types
open Box

open System
open System.Net
open System.Net.Http

open Microsoft.Azure.WebJobs
open Microsoft.Azure.WebJobs.Extensions.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging

/// Read in the app setttings from the environment or a local.settings.json file.
let config = 
    ConfigurationBuilder()
        .AddJsonFile("local.settings.json", optional=true)
        .AddEnvironmentVariables()
        .Build();

/// Authorize requests against the configured secret.
let authorize =  config.GetValue("Secret") |> authorize
let boxConfig = config.GetValue("BoxConfig")
let containerFolderId = config.GetValue("BoxContainerFolderId")
let templateFolderId = config.GetValue("BoxTemplateFolderId")
let doBoxStuff =  (boxConfig, containerFolderId, templateFolderId) |> boxPipeline

/// A simple ping/pong function for availability checks.        
[<FunctionName("PingGet")>]
let ping
    ([<HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")>] req:HttpRequestMessage) =
    req.CreateResponse(HttpStatusCode.OK, "pong!")

/// A webhook to receive Data Steward request submissions. This function will validate the submission,
/// enqueue it to be worked, and return a 200 OK if successful.
[<FunctionName("WebhookGet")>]
let webhook
    ([<HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhook")>] req:HttpRequestMessage,
     [<Queue("request-submission")>] queue: ICollector<string>,
     logger:ILogger) = 

    let pipeline = 
        authorize
        >=> readBody
        >=> tap (sprintf "Received request body: %s" >> logger.LogInformation)
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