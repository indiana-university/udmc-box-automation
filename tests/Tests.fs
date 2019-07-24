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
        FileUrl=""
        FileName=""
        VendorName="VendorY"
        ProductName="ProductY"
        Campus="BL"
        DeptCode="Department"
        SsspNumber="1234567"
        Assignees="mestell@iu.edu"
        ItPro="jhoerr@umail-test.iu.edu"
        PciExists="Yes."
        Treasury="" }

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
   