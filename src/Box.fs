module Box

open System
open System.Net.Http

open Box.V2
open Box.V2.Models
open Box.V2.Models.Request
open Box.V2.Config
open Box.V2.JWTAuth

open Types
open ROP

let await = Async.AwaitTask
let httpClient = (new HttpClient())
let caseInsensitive = StringComparison.InvariantCultureIgnoreCase
let trimDomain (username:string) = if username.Contains('@') then username.Split('@').[0] else username
let addDomain (username:string) = if username.Contains('@') then username else sprintf "%s@iu.edu" username

let datastewardTaskNote (s:Submission) folderId = 
    sprintf """A data handling request has been submitted for:

Software/Service: %s
Vendor: %s
Department: %s-%s
Requestor: %s %s (%s)
SSSP #: %s

A risk assessment folder containing information and documents pertaining to this request has been created in Box: https://app.box.com/folder/%s

You have been tasked with this request because your data domain was indicated in the request. You should have received a copy of the data-handling request via email. Feel free to communicate your needs directly to the Requestor in coordination with the other Data Stewards.""" 
        s.ProductName s.VendorName s.Campus s.DeptCode s.SubmitterFirstName s.SubmitterLastName s.SubmitterEmail s.SsspNumber folderId

let itProComment (s:Submission) folderId = 
    sprintf """%s %s (%s) has submitted a request to the Data Stewards for:

Software/Service: %s
Vendor: %s
Department: %s-%s
SSSP #: %s

You have received this note because the Requester has identified you as their IT Pro. A risk assessment folder containing information and documents pertaining to this request has been created in Box: https://app.box.com/folder/%s.
You have been invited to collaborate on this folder and may be requested to provide technical/implementation details for the Data Stewards to conduct their review. Thanks in advance for your help!""" 
        s.SubmitterFirstName s.SubmitterLastName s.SubmitterEmail s.ProductName s.VendorName s.Campus s.DeptCode s.SsspNumber folderId

let requestorComment (s:Submission) submissionFolderId uploadFolderId = 
    sprintf """Thank you for submitting your request for %s %s. The Data Stewards have received your request and started their review. 

You will eventually receive a Risk Assessment Report which includes the Data Stewards' approval, conditional approval, or disapproval of your request. In the mean time they may contact you for additional information. 

A risk assessment folder containing information and documents pertaining to this request has been created in Box: https://app.box.com/folder/%s.
If you need to upload any additional files or information, please do so here: https://app.box.com/folder/%s.

Note: If any institutional data classified as 'critical' or 'restricted' is involved in this request, you will likely be required to submit a UISO Risk Assessment Report. You can find more information on the Risk Assessment here: <INSERT URL>"""
        s.VendorName s.ProductName submissionFolderId uploadFolderId

/// Get a Box client authenticated as the JWT app service account.
let getClient config = async {
    let auth = 
        config 
        |> Convert.FromBase64String
        |> Text.Encoding.UTF8.GetString
        |> BoxConfig.CreateFromJsonString 
        |> BoxJWTAuth
    return auth.AdminToken() |> auth.AdminClient
}

/// Get all items from a Box folder.
let getItems (client:BoxClient) folderId = 
    client.FoldersManager.GetFolderItemsAsync(id=folderId, limit=1000, offset=0) |> await

/// Try to find an existing item by name in a Box folder.
let tryFindItem (client:BoxClient) folderId name = async {
    let! items = getItems client folderId
    return items.Entries |> Seq.tryFind (fun i -> i.Name = name)
}

/// Create a copy of the submission template folder named for this submission. 
/// Do not overwrite an existing submission folder.
let createSubmissionFolder (client:BoxClient) (sub:Submission) containerFolderId templateFolderId  = async { 
    let name = sprintf "%s - %s - %s - %s - %s" sub.VendorName sub.ProductName sub.Campus sub.DeptCode sub.SsspNumber
    let! maybeFolder = tryFindItem client containerFolderId name
    match maybeFolder with
    | Some (folder) -> return folder
    | None -> 
        let parent = BoxRequestEntity(Id=containerFolderId)
        let req = BoxFolderRequest(Id=templateFolderId, Name=name, Parent=parent)
        let! folder = req |> client.FoldersManager.CopyAsync |> await
        return folder :> BoxItem
}

/// Create a bookmark to the survey submission report in the submission folder. 
/// Do not overwrite an existing bookmark.
let createSubmissionBookmark (client:BoxClient) (sub:Submission) (folder:BoxItem) = async {
    let name = "Survey Response"
    let! maybeBookmark = tryFindItem client folder.Id name
    match maybeBookmark with
    | Some (bookmark) -> return bookmark
    | None ->
        let parent = BoxRequestEntity(Id=folder.Id)
        let req = BoxWebLinkRequest(Name="Survey Response", Url=Uri(sub.ReportUrl), Parent=parent)
        let! bookmark = client.WebLinksManager.CreateWebLinkAsync(req) |> await
        return bookmark :> BoxItem
}

/// Fetch the file uploaded to Qualtrics (if any) and add it to the submission folder. 
/// Do not overwite an existing file of the same name.
let createSupplementFile (client:BoxClient) (sub:Submission) (folder:BoxItem) = async {
    if String.IsNullOrWhiteSpace(sub.FileName)
    then return None
    else 
        let url = 
            if sub.FileUrl.StartsWith("https")
            then sub.FileUrl
            else sprintf "https://iu.iad1.qualtrics.com/%s" (sub.FileUrl.TrimStart([|'/'|]))
        let! maybeFile = tryFindItem client folder.Id sub.FileName
        match maybeFile with
        | Some(file) -> return Some(file)
        | None ->            
            let! stream = httpClient.GetStreamAsync(url) |> Async.AwaitTask
            use memoryStream = (new IO.MemoryStream())
            do! stream.CopyToAsync(memoryStream) |> Async.AwaitTask
            let parent = BoxRequestEntity(Id=folder.Id)
            let req = BoxFileRequest(Name=sub.FileName, Parent=parent)
            let! resp = (req, memoryStream) |> client.FilesManager.UploadAsync |> Async.AwaitTask
            return resp :> BoxItem |> Some
}

/// Create a task and message on the specified template file. 
/// Do not overwrite an existing task.
let createTask (client:BoxClient) (sub:Submission)  (folder:BoxItem) (template:BoxItem) = async {
    let! tasks = client.FilesManager.GetFileTasks(template.Id) |> await
    if tasks.TotalCount = 1
    then return tasks.Entries.[0]
    else 
        let item = BoxRequestEntity(Id=template.Id, Type=Nullable(BoxType.file))
        let message = datastewardTaskNote sub folder.Id
        let req = BoxTaskCreateRequest(Item=item, Message=message)
        return! req |> client.TasksManager.CreateTaskAsync |> await
}

/// Assign the task to the specified logins. 
let assignTasks (client:BoxClient) (task:BoxTask) logins = async {
    let taskReq = BoxTaskRequest(Id=task.Id)
    let createAssignment login = async {
        let assignReq = BoxAssignmentRequest(Login=login)
        let req = BoxTaskAssignmentRequest(Task=taskReq, AssignTo=assignReq)
        return! req |> client.TasksManager.CreateTaskAssignmentAsync |> await            
    }
    let! assignments = client.TasksManager.GetAssignmentsAsync(task.Id) |> await
    let assignedLogins = assignments.Entries |> Seq.map (fun a -> a.AssignedTo.Login)
    let unassignedLogins = logins |> Seq.except assignedLogins
    return! unassignedLogins |> Seq.map createAssignment |> Async.Parallel        
}

/// Create 'viewer uploader' collaborations on the submission folder with the specified login and tagged message.
let createCollaborationAndComment (client:BoxClient) (folder:BoxItem) (file:BoxItem) login comment = async {
    try            
        let user = BoxCollaborationUserRequest(Login=login)
        let collabItem = BoxRequestEntity(Id=folder.Id, Type=Nullable(BoxType.folder))
        let! collab =  
            BoxCollaborationRequest(Item=collabItem, AccessibleBy=user, Role="viewer uploader") 
            |> client.CollaborationsManager.AddCollaborationAsync 
            |> await
        let commentItem = BoxRequestEntity(Id=file.Id, Type=Nullable(BoxType.file))
        let taggedMessage = sprintf "@[%s:%s] %s" collab.AccessibleBy.Id login comment 
        let! _ =
            BoxCommentRequest(Item=commentItem, TaggedMessage=taggedMessage)
            |> client.CommentsManager.AddCommentAsync 
            |> await
        return collab
    with
    | exn -> 
        if exn.Message.Contains ("user_already_collaborator") |> not
        then raise exn            
        return Unchecked.defaultof<BoxCollaboration>
}

let createRequestorCollaboration (client:BoxClient) (submission:Submission) (submissionFolder:BoxItem) (templateFile:BoxItem) (uploadFolder:BoxItem) =
    let login = addDomain submission.SubmitterEmail
    let comment = requestorComment submission submissionFolder.Id uploadFolder.Id
    createCollaborationAndComment client submissionFolder templateFile login comment

let createItProCollaboration (client:BoxClient) (submission:Submission) (submissionFolder:BoxItem) (templateFile:BoxItem) = async {
    if String.IsNullOrWhiteSpace(submission.ItPro) 
    then 
        return Unchecked.defaultof<BoxCollaboration>
    else 
        let login = addDomain submission.ItPro
        let comment = itProComment submission submissionFolder.Id
        return! createCollaborationAndComment client submissionFolder templateFile login comment
}

let processAssignees (s:Submission) = 
    let s = if String.IsNullOrWhiteSpace(s.Assignees) then "" else s.Assignees
    s.Split([|','; ';'|])
    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
    |> Seq.map (fun s -> s.Trim())
    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
    |> Seq.map addDomain
    |> Seq.toList


/// A pipeline that describes the Box workflow automation for generating request submssion folders, assigning tasks, and creating collaborations.
let boxPipeline (config, containerFolderId, templateFolderId) (log:string->unit) (submission:Submission) = async {

    let assignees = submission |> processAssignees
        
    // Get an authenticated Box client
    let! client = (getClient config) |> exec log "Authenticate Box client"
    // Copy the submission folder from the existing folder template
    let! submissionFolder = (createSubmissionFolder client submission containerFolderId templateFolderId)  |> exec log "Create submission folder"
    // Locate the preview template document and the file upload folder in the new submission folder
    let! submissionFolderItems = getItems client submissionFolder.Id
    let previewTemplate = submissionFolderItems.Entries |> Seq.find (fun e -> e.Name.Contains("preview", caseInsensitive))
    let uploadFolder = submissionFolderItems.Entries |> Seq.find (fun e -> e.Name.Contains("uploads", caseInsensitive))
    // Add a bookmark to the Qualtrics survey report
    let! _ = (createSubmissionBookmark client submission submissionFolder) |> exec log "Create submission bookmark"
    // Fetch any supplmental files from Qualtrics and upload them to Box
    let! _ = (createSupplementFile client submission uploadFolder) |> exec log "Create supplement file"
    // Create collaborations with the requestor and other interested folks.
    let! _ = (createRequestorCollaboration client submission submissionFolder previewTemplate uploadFolder) |> exec log "Create requestor collaboration"
    let! _ = (createItProCollaboration client submission submissionFolder previewTemplate) |> exec log "Create IT Pro collaboration"

    return Ok submission
}
