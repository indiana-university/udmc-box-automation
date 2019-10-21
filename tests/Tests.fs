module Tests

open System
open Xunit

open Box
open Types

[<Fact>]
let ``Box Integration test`` () =
    
    let submission : Submission = 
      { SubmitterFirstName="John"
        SubmitterLastName="Hoerr"
        SubmitterEmail="jhoerr@iu.edu"
        ReportUrl="https://iu.co1.qualtrics.com/CP/Report.php?SID=SV_e9CXE97UrWdBR3f&R=R_sMqmEMf6QNZnxol"
        FileUrl=""
        FileName=""
        VendorName="Vendor"
        ProductName="Product " + DateTime.Now.ToShortTimeString()
        Campus="BL"
        DeptCode="Department"
        SsspNumber="1234567"
        LookupUsers="1: mestell; 2: jhoerr"
        Assignees="1, 2"
        DataDomains="Peanut Butter, Jelly"
        ItPro=""
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

[<Fact>]
let ``lookup table`` () =
  let lookupUsers = "1:foo, mestell; 2: mestell;3:jhoerr; 4: bar,baz";
  let processAssignees = Box.enumerateTaskAssignees lookupUsers
  Assert.True((processAssignees "1, 2") = ["foo"; "mestell"])
  Assert.True((processAssignees "2,3") = ["mestell"; "jhoerr"])
  Assert.True((processAssignees "2, 1000") = ["mestell"])
  ()

