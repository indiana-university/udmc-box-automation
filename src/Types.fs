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
    Campus: string
    SsspNumber: string
    Assignees: string
    DataDomains: string
    LookupUsers: string
    ItPro: string 
    Treasury: string
    PciExists: string }
