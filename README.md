# UDMC Qualtrics+Box Automation

The Data Stewards receive requests for usage of University data through a Qualtrics survey form. Upon completion of the survey Qualtrics can trigger different kinds of tasks, including email notification and [web service](https://www.qualtrics.com/support/survey-platform/actions-module/web-service-task/) (HTTP API endpoint) invocation. This respository defines a web service to automate the following workflow :

1. A Box folder is created on the basis of each incoming request. The folder is named on the basis of properties in the webservice request body.
1. Several document templates are copied into this folder, to filled out by the appropriate data stewards. 
1. A Box bookmark is created that links back to the survey submission data. 
1. The requestor, their IT Pro, and IU Treasury (if PCI is indicated), are collaborated into this Box folder so that they can provide additional documentation/files and review the reports.


## Pre-requisites

This web service is build on the serverless Azure Functions platform. To build this web service, test it locally, and publish it to Azure, download and install:

* [.NET Core 2.2 SDK](https://dotnet.microsoft.com/download/dotnet-core/2.2)
* [azure-functions-core-tools](https://github.com/Azure/azure-functions-core-tools).

## Configuration

The following JSON should be placed in a file, `src/local.settings.json`. This file is gitignored.

```
{
    "IsEncrypted": false,
    "Values": {
        "AzureWebJobsStorage": "Required: An Azure Storage connection string",
        "AzureWebJobsDashboard": "Required: An Azure Storage connection string",
        "Secret": "Required: A shared secret, to be used as a Bearer token by Qualtrics when calling the webhook",
        "BoxConfig": "Required: A Base 64 encoded string representing the contents of a Box JWT app config JSON file",
        "BoxContainerFolderId": "Required: The ID of a Box folder, in which submissions folders should be created",
        "BoxTemplateFolderId": "Required: The ID of a Box folder, containing the template files for the submission review",
        "APPINSIGHTS_INSTRUMENTATIONKEY": "Optional: An Azure Application Insights instrumentation key (GUID)",
        "FUNCTIONS_WORKER_RUNTIME": "Required: dotnet"
    }
}
```

## Webservice Request Body Description

```
{ 
    "SubmitterFirstName":"Required: The subitter's preferred first name",
    "SubmitterLastName": "Required: The submitter's preferred last name",
    "SubmitterEmail": "Required: The submitter's IU email address",
    "Campus": "Required: The submitter's campus code (e.g. 'BL')",
    "DeptCode": "Required: The submitter's department code (e.g. 'UITS')",
    "ReportUrl": "Required: A URL pointing to a formatted version of the request submission data",
    "VendorName": "Required: The name of the vendor of the requested product",
    "ProductName": "Required: The name of the requested product/service",
    "SsspNumber": "Required: The Software Services Selection Process (SSSP) number associated with this request",
    "FileUrl": "Optional: A URL pointing to an (optional) file uploaded with the request submission",
    "FileName": "Optional: The name of the (optional) uploaded file",
    "Assignees": "Optional: The Data Steward data domains (comma-separated) indicated with this request",
    "ItPro": "Optional: The IU email address of the submitter's IT Pro",
    "PciExists": "Optional: Whether PCI (payment card) infromation is indicated in this request.",
    "Treasury": "Optional: A comma-separated list of usernames of Treasury staff to be collaborated in, if PCI is indicated."
}
```

## Building

From the `src` folder, run `dotnet build`.
