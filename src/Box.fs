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
    let name = sprintf "%s - %s - %s - %s" sub.VendorName sub.ProductName sub.DeptCode sub.SsspNumber
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
        let message = sprintf """%s %s (%s) submitted an SSSP request. 
        Submission folder:  https://app.box.com/folder/%s""" sub.SubmitterFirstName sub.SubmitterLastName sub.SubmitterEmail folder.Id
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

/// Create 'viewer uploader' collaborations on the submission folder with the specified logins.
let createCollaborations (client:BoxClient) (folder:BoxItem) logins = async {
    let item = BoxRequestEntity(Id=folder.Id, Type=Nullable(BoxType.folder))
    let createCollaboration login = async {
        try            
            let user = BoxCollaborationUserRequest(Login=login)
            let req = BoxCollaborationRequest(Item=item, AccessibleBy=user, Role="viewer uploader")
            return! req |> client.CollaborationsManager.AddCollaborationAsync |> await            
        with
        | exn -> 
            if exn.Message.Contains ("user_already_collaborator") |> not
            then raise exn            
            return Unchecked.defaultof<BoxCollaboration>
    }
    let! collaborations = client.FoldersManager.GetCollaborationsAsync(folder.Id) |> await
    let assignedLogins = collaborations.Entries |> Seq.map (fun c -> c.InviteEmail)
    let unaassignedLogins = logins |> Seq.except assignedLogins
    return! unaassignedLogins |> Seq.map createCollaboration |> Async.Parallel            
}


/// A pipeline that describes the Box workflow automation for generating request submssion folders, assigning tasks, and creating collaborations.
let boxPipeline (config, containerFolderId, templateFolderId) (log:string->unit) (submission:Submission) = async {

    let assignees = ["jhoerr@iu.edu" ]
    let collaborators = [submission.SubmitterEmail]
    
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
    // Create an assign tasks on the template document.
    let! task = (createTask client submission submissionFolder previewTemplate) |> exec log "Create task"
    let! _ = (assignTasks client task assignees) |> exec log "Assign tasks to data stewards"
    // Create collaborations with the requestor and other interested folks.
    let! _ = (createCollaborations client submissionFolder collaborators) |> exec log "Create collaboration with requestor"

    return Ok submission
}
