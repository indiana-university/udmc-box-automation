module Tests

open System
open Xunit

open Box
open Types

[<Fact>]
let ``Box Integration test`` () =
    
    let submission = 
      { SubmitterFirstName="John"
        SubmitterLastName="Hoerr"
        SubmitterEmail="jhoerr@iu.edu"
        ReportUrl="CHANGEME"
        FileUrl="CHANGEME"
        FileName="Supplements.md"
        VendorName="Vendor"
        ProductName="ProductX"
        DeptCode="Department"
        SsspNumber="1234567"
        DataDomains="foo, bar" }

    let env key = 
      let value = key |> System.Environment.GetEnvironmentVariable
      Assert.NotNull(value)
      value

    let boxConfig = env "BoxConfig"
    let containerfolderId = env "BoxContainerFolderId"
    let templateFolderId = env "BoxTemplateFolderId"

    let log = printfn "%s"
    let args = (boxConfig, containerfolderId, templateFolderId)

    boxPipeline args log submission |> Async.RunSynchronously |> ignore
    ()