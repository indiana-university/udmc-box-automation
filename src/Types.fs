module Types

[<CLIMutable>]
type Submission =
  { SubmitterFirstName : string
    SubmitterLastName : string
    SubmitterEmail : string
    ReportUrl : string
    FileUrl : string
    FileName : string
    VendorName: string
    ProductName: string
    DeptCode: string
    SsspNumber: string
    DataDomains: string }
