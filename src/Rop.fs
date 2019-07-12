module ROP

open System
open System.Net
open System.Net.Http

// Evaluate 'a'. If it succeeds, unwrap the result and pass it to 'f'. If it fails, return an Error. 
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

// An async Ok
let ok obj = obj |> Ok |> async.Return

// An async Error
let error(status, msg) = Error(status, msg) |> async.Return  

let tap f x =
    f x // invoke f with the argument x
    ok x // pass x unchanged to the next step in the workflow

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

/// Attempt to execute an Async action with a single retry.
/// Log the attempt, success, and failure of the action. 
let exec log action (fn:Async<'T>) = async {
    let exec' () = async {
        let! result = fn
        sprintf "[Success] %s" action |> log
        return result
    }
    try 
        sprintf "[Attempt] %s" action |> log
        return! exec' ()
    with
    | exn -> 
        try 
            sprintf "[Retry] %s" action |> log
            return! exec' ()
        with
        | exn -> 
            sprintf "[Error] %s: %s" action exn.Message |> log
            raise exn
            return Unchecked.defaultof<'T>
}
