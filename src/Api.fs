module API

open ROP

open System
open System.Net
open System.Net.Http

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
    then error (HttpStatusCode.BadRequest, "Expected a request body but received nothing")
    else 
        try JsonConvert.DeserializeObject<'T>(str) |> ok
        with exn -> error (HttpStatusCode.BadRequest, exn.Message)

/// Attempt to deserialize the request body as an object of the given type.
let readBody (req:HttpRequestMessage) = async { 
    let! body = req.Content.ReadAsStringAsync() |> Async.AwaitTask
    return Ok body
}


/// Attempt to deserialize the request body as an object of the given type.
let deserializeBody<'T> body = async { 
    return! deserialize<'T> body
}

let serialize obj = JsonConvert.SerializeObject(obj, JsonSettings)
