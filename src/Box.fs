module Box

open System
open System.Net.Http
open Newtonsoft.Json

open Box.V2
open Box.V2.Models
open Box.V2.Models.Request
open Box.V2.Config
open Box.V2.JWTAuth

open Types
open ROP


type BoxTaskCreateRequestAlt() =
   inherit BoxTaskCreateRequest()
   member this.Action = "complete"

let await = Async.AwaitTask
let httpClient = (new HttpClient())
let caseInsensitive = StringComparison.InvariantCultureIgnoreCase
let trimDomain (username:string) = if username.Contains('@') then username.Split('@').[0] else username

let datastewardTaskNote (s:Submission) folderId = 
    sprintf """A data handling request has been submitted by %s %s (%s) for %s. The following data domains were indicated in this request: %s. A 3PA review folder containing information and documents pertaining to this request has been created in Box: https://app.box.com/folder/%s.""" 
        s.SubmitterFirstName s.SubmitterLastName s.SubmitterEmail s.ProductName s.DataDomains folderId

/// Get a Box client authenticated as the JWT app service account.
let getClient config = async {
    let auth = config 
               |> Convert.FromBase64String
               |> Text.Encoding.UTF8.GetString
               |> BoxConfig.CreateFromJsonString 
               |> BoxJWTAuth
    return auth.AdminToken() |> auth.AdminClient
}

let tryFindLogin (client:BoxClient) username = async {
    let filterTerm = sprintf "%s@" username
    let! result = client.UsersManager.GetEnterpriseUsersAsync(filterTerm=filterTerm) |> await
    match result.TotalCount with
    | 0 -> return None
    | _ -> return Some(result.Entries.[0])
}

let tryFindUsers (client:BoxClient) usernames = async {
    let! logins = usernames
                  |> Seq.filter (String.IsNullOrWhiteSpace >> not) 
                  |> Seq.map trimDomain
                  |> Seq.distinct
                  |> Seq.map (tryFindLogin client)
                  |> Async.Parallel
    return logins 
           |> Seq.filter (fun l -> l.IsSome)    
           |> Seq.map (fun l -> l.Value)
}

/// Get all items from a Box folder.
let getItems (client:BoxClient) folderId = 
    client.FoldersManager.GetFolderItemsAsync(id=folderId, limit=1000, offset=0) |> await

let hasItems (client:BoxClient) folderId = async { 
    let! items = getItems client folderId
    return items.TotalCount <> 0
}

/// Try to find an existing item by name in a Box folder.
let tryFindItem (client:BoxClient) folderId name = async {
    let! items = getItems client folderId
    return items.Entries |> Seq.tryFind (fun i -> i.Name = name)
}

/// Create a copy of the submission template folder named for this submission. 
/// Do not overwrite an existing submission folder.
let createSubmissionFolder (client:BoxClient) (sub:Submission) containerFolderId templateFolderId  = async { 
    let name = sprintf "%s - %s - %s - %s" sub.ProductName sub.VendorName sub.Campus sub.DeptCode
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
    let url = sprintf "%s&NoStatsTables=1&ResponseSummary=True" sub.ReportUrl |> Uri
    let! maybeBookmark = tryFindItem client folder.Id name
    match maybeBookmark with
    | Some (bookmark) -> return bookmark
    | None ->
        let parent = BoxRequestEntity(Id=folder.Id)
        let req = BoxWebLinkRequest(Name="Survey Response", Url=url, Parent=parent)
        let! bookmark = client.WebLinksManager.CreateWebLinkAsync(req) |> await
        return bookmark :> BoxItem
}

/// Fetch the file uploaded to Qualtrics (if any) and add it to the submission folder. 
/// Do not overwite an existing file of the same name.
let tryCreateSupplementFile (client:BoxClient) (sub:Submission) (folder:BoxItem) = async {
    // check to see if a file was submitted with the request. 
    // if not, bail.
    if String.IsNullOrWhiteSpace(sub.FileUrl)
    then return None
    else 
        // check to see if a file is already present in the uploads folder. 
        // if so, return that.
        let! items = getItems client folder.Id
        if items.TotalCount <> 0
        then return Some(items.Entries.[0])
        else 
            // fetch the file from qualtrics
            let url = 
                if sub.FileUrl.StartsWith ("https")
                then sub.FileUrl
                else sprintf "https://iu.iad1.qualtrics.com/%s" (sub.FileUrl.TrimStart([|'/'|]))
            let! response = httpClient.GetAsync(url) |> await
            // copy the content stream to a memory stream so the Box client can manage the stream position.
            let! stream = response.Content.ReadAsStreamAsync() |> await
            use memoryStream = new IO.MemoryStream()
            do! stream.CopyToAsync(memoryStream) |> Async.AwaitTask
            let name = response.Content.Headers.ContentDisposition.FileName
            // upload the file to box.
            let parent = BoxRequestEntity(Id=folder.Id)
            let req = BoxFileRequest(Name=name, Parent=parent)
            let! resp = (req, memoryStream) |> client.FilesManager.UploadAsync |> await
            return resp :> BoxItem |> Some 
}

let sendManualBoxRequest<'T> (client:BoxClient) method uri body = async {
    let json = body |> JsonConvert.SerializeObject
    let req = new HttpRequestMessage(RequestUri=Uri(uri), Method=method, Content=new StringContent(json))
    req.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", client.Auth.Session.AccessToken)
    let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
    resp.EnsureSuccessStatusCode() |> ignore
    let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    return respBody |> JsonConvert.DeserializeObject<'T>
}

/// Create a task and message on the specified template file. 
/// Do not overwrite an existing task.
let createTask (client:BoxClient) (sub:Submission)  (folder:BoxItem) (template:BoxItem) = async {
    let! tasks = client.FilesManager.GetFileTasks(template.Id) |> await
    if tasks.TotalCount = 1
    then return tasks.Entries.[0]
    else 
        let uri = "https://api.box.com/2.0/tasks"
        let message = datastewardTaskNote sub folder.Id
        let body = {| item={| ``type``="file"; id=template.Id |}; message=message; action="complete" |}
        return! sendManualBoxRequest<BoxTask> client HttpMethod.Post uri body
}

let existingAssignments (client:BoxClient) (task:BoxTask) =
    client.TasksManager.GetAssignmentsAsync(task.Id) |> await

/// Assign the task to the specified logins. 
let assignTasks (client:BoxClient) (task:BoxTask) (users:seq<BoxUser>) = async {
    let taskReq = BoxTaskRequest(Id=task.Id)
    let assignTask id = async {
        let assignReq = BoxAssignmentRequest(Id=id)
        let req = BoxTaskAssignmentRequest(Task=taskReq, AssignTo=assignReq)
        return! req |> client.TasksManager.CreateTaskAssignmentAsync |> await            
    }
    return! users |> Seq.map (fun u -> assignTask u.Id) |> Async.Parallel        
}

let unassignedUsers (users:seq<BoxUser>) (assignments:BoxCollection<BoxTaskAssignment>) =
    let assignedIds = assignments.Entries |> Seq.map (fun a -> a.AssignedTo.Id)
    let unassignedIds = users |> Seq.map (fun u -> u.Id) |> Seq.except assignedIds
    users |> Seq.filter (fun u -> unassignedIds |> Seq.contains u.Id)

/// Create 'viewer uploader' collaborations on the submission folder with the specified login and tagged message.
let createCollaboration (client:BoxClient) (folder:BoxItem) id = async {
    try            
        let user = BoxCollaborationUserRequest(Id=id)
        let collabItem = BoxRequestEntity(Id=folder.Id, Type=Nullable(BoxType.folder))
        return!
            BoxCollaborationRequest(Item=collabItem, AccessibleBy=user, Role="viewer uploader") 
            |> client.CollaborationsManager.AddCollaborationAsync 
            |> await
    with
    | exn -> 
        if exn.Message.Contains ("user_already_collaborator") |> not
        then raise exn            
        return Unchecked.defaultof<BoxCollaboration>
}

let existingCollaborations (client:BoxClient) (folder:BoxItem) =
    client.FoldersManager.GetCollaborationsAsync(folder.Id) |> await

let uncollaboratedUsers (users:seq<BoxUser>) (collabs:BoxCollection<BoxCollaboration>) = 
    let collaboratedIds = collabs.Entries |> Seq.map (fun c -> c.AccessibleBy.Id)
    let uncollaboratedIds = users |> Seq.map (fun u -> u.Id) |> Seq.except collaboratedIds
    users |> Seq.filter (fun u -> uncollaboratedIds |> Seq.contains u.Id)

let createCollaborations (client:BoxClient) (folder:BoxItem) (users:seq<BoxUser>) =
    users 
    |> Seq.map (fun u -> createCollaboration client folder u.Id)
    |> Async.Parallel

let parseDelimitedList str = 
    let s = if String.IsNullOrWhiteSpace(str) then "" else str
    s.Split([|','; ';'|])
    
    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
    |> Seq.map (fun s -> s.Trim())
    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
    |> Seq.toList
 
let pciExists (sub:Submission) =
    not (String.IsNullOrWhiteSpace(sub.PciExists)) && 
    sub.PciExists.Contains("yes", StringComparison.InvariantCultureIgnoreCase)

let parseDataStewardLookupTable (str:string) =
    str.Split(";")
    |> Seq.map(fun s -> 
        let parts = s.Split(":")
        let key = parts.[0].Trim()
        let vals = parts.[1].Split(",") |> Seq.map (fun s -> s.Trim()) |> Seq.toList
        key, vals)
    |> dict    

let enumerateTaskAssignees (lookupUsers:string) (assignees:string)  = 
    let assigneeKeys = assignees |> parseDelimitedList
    let lookupTable = lookupUsers |> parseDataStewardLookupTable    
    assigneeKeys
    |> Seq.collect (fun a -> if lookupTable.ContainsKey(a) then lookupTable.[a] else [])
    |> Seq.distinct
    |> Seq.toList

let enumerateCollaborators (sub:Submission) =
    let list = [sub.SubmitterEmail]
    let list = 
        if sub.ItPro |> String.IsNullOrWhiteSpace |> not
        then list @ [sub.ItPro]
        else list
    let list =
        if sub |> pciExists 
        then list @ (parseDelimitedList sub.Treasury)
        else list
    list

/// A pipeline that describes the Box workflow automation for generating request submssion folders, assigning tasks, and creating collaborations.
let boxPipeline (config, containerFolderId, templateFolderId) (log:string->unit) (submission:Submission) = async {
    
    // Get an authenticated Box client
    let! client = (getClient config) |> exec log "Authenticate Box client"
    // Copy the submission folder from the existing folder template
    let! submissionFolder = (createSubmissionFolder client submission containerFolderId templateFolderId)  |> exec log "Create submission folder"
    // Locate the preview template document and the file upload folder in the new submission folder
    let! submissionFolderItems = getItems client submissionFolder.Id
    // Add a bookmark to the Qualtrics survey report
    let! _ = (createSubmissionBookmark client submission submissionFolder) |> exec log "Create submission bookmark"
    // Fetch any supplmental files from Qualtrics and upload them to Box
    // let uploadFolder = submissionFolderItems.Entries |> Seq.find (fun e -> e.Name.Contains("uploads", caseInsensitive))
    // let! _ = (tryCreateSupplementFile client submission uploadFolder) |> exec log "Create supplement file"
    
    // Create and assign tasks to Data Stewards
    let previewTemplate = submissionFolderItems.Entries |> Seq.find (fun e -> e.Name.Contains("preview", caseInsensitive))
    let! task = (createTask client submission submissionFolder previewTemplate) |> exec log "Create task"
    let! desiredAssignees = enumerateTaskAssignees submission.LookupUsers submission.Assignees |> tryFindUsers client |> exec log (sprintf "Get desired assignee logins")
    let! existingAssignments = existingAssignments client task |> exec log ("Get existing task assignmnents")
    let unassignedUsers = unassignedUsers desiredAssignees existingAssignments 
    let! _ = (assignTasks client task unassignedUsers) |> exec log (unassignedUsers |> Seq.map (fun a -> (a.Login, a.Id)) |> sprintf "Assign task to: %A" )
    
    // Create collaborations with the requestor, IT Pro (if indicated), and Treasury (if PCI indicated) 
    let! desiredCollaborators = enumerateCollaborators submission |> tryFindUsers client |> exec log (sprintf "Get desired collaborator logins")
    let! existingCollabs = (existingCollaborations client submissionFolder) |> exec log "Get existing collaborations"
    let uncollabedUsers = uncollaboratedUsers desiredCollaborators existingCollabs
    let! _ = (createCollaborations client submissionFolder uncollabedUsers) |> exec log (uncollabedUsers |> Seq.map (fun a -> (a.Login, a.Id)) |> sprintf "Create collaborations with: %A")

    return Ok submission
}