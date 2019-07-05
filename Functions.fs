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
      { Name : string
        Submitter : string }

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



module Functions =    

    open API
    open ROP
    open Types
  
    open Microsoft.Azure.WebJobs
    open Microsoft.Azure.WebJobs.Extensions.Http
    open Microsoft.Azure.WebJobs.Extensions.Storage
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
                |> System.Exception
                |> raise
        } |> Async.StartAsTask

    /// A simple ping/pong function for availability checks.        
    [<FunctionName("PingGet")>]
    let ping
        ([<HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")>] req:HttpRequestMessage) =
        req.CreateResponse(HttpStatusCode.OK, "pong!")

    /// A webhook to receive Data Steward request submissions. This function will validate the submission,
    /// enqueue it to be worked, and return a 200 OK if successful.
    [<FunctionName("WebhookGet")>]
    let webhook
        ([<HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "webhook")>] req:HttpRequestMessage,
         [<Queue("request-submission")>] queue: ICollector<string>,
         logger:ILogger) = 

        let enqueue = serialize >> queue.Add
        let log = sprintf "Enqueued submission of %A" >> logger.LogInformation
        
        let pipeline = 
            authorize
            >=> deserializeBody<Submission>
            >=> tap enqueue
            >=> tap log
           
        req |> http pipeline 

    /// A function to receive the submission message from the queue and process it.
    [<FunctionName("QueueWorker")>]
    let queueWorker
        ([<QueueTrigger("request-submission")>] item: string,
         logger: ILogger) = 
         
         let log = sprintf "Dequeued submission of: %A" >> logger.LogInformation
         
         let pipeline =  
            deserialize<Submission>
            >=> tap log
         
         item |> task pipeline
