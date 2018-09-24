using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;

namespace api
{
    public static class TodoItems
    {
        private static AuthorizedUser GetCurrentUserName(HttpRequest req, ILogger log)
        {
            // On localhost claims will be empty
            string name = "Dev User";
            string upn = "dev@localhost";

            var username = req.Headers["X-MS-CLIENT-PRINCIPAL-NAME"];    

            if (StringValues.Empty != username)
            {
                name = username;
                upn = username;
            }
            return new AuthorizedUser() { DisplayName = name, UniqueName = upn };
        }

        // Add new item
        [FunctionName("TodoItemAdd")]
        public async static Task<IActionResult> AddItem(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "todoitem")]HttpRequest req,
            [CosmosDB("ServerlessTodo", "TodoItems", ConnectionStringSetting = "CosmosDBConnectionString")] IAsyncCollector<TodoItem> newTodoItems,
            ILogger log)
        {
            // Get request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            TodoItem newItem = JsonConvert.DeserializeObject<TodoItem>(requestBody);

            log.LogInformation($"Upserting item: {newItem.ItemName}");
            if (string.IsNullOrEmpty(newItem.id))
            {
                // New Item so add ID and date
                log.LogInformation("Item is new.");
                newItem.id = Guid.NewGuid().ToString();
                newItem.ItemCreateDate = DateTime.Now;
                newItem.ItemOwner = GetCurrentUserName(req, log).UniqueName;
            }
            await newTodoItems.AddAsync(newItem);

            return new OkObjectResult(newItem);
        }

        // Get all items
        [FunctionName("TodoItemGetAll")]
        public static IActionResult GetAll(
           [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "todoitem")]HttpRequest req,
           [CosmosDB("ServerlessTodo", "TodoItems", ConnectionStringSetting = "CosmosDBConnectionString")] DocumentClient client,
           ILogger log)
        {
            var currentUser = GetCurrentUserName(req, log);
            log.LogInformation($"Getting all Todo items for user: {currentUser.UniqueName}");

            Uri collectionUri = UriFactory.CreateDocumentCollectionUri("ServerlessTodo", "TodoItems");

            var itemQuery = client.CreateDocumentQuery<TodoItem>(collectionUri).Where(i => i.ItemOwner == currentUser.UniqueName);

            var ret = new { UserName = currentUser.DisplayName, Items = itemQuery.ToArray() };

            return new OkObjectResult(ret);
        }

        // Delete item by id
        [FunctionName("TodoItemDelete")]
        public static async Task<IActionResult> DeleteItem(
           [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "todoitem/{id}")]HttpRequest req,
           [CosmosDB("ServerlessTodo", "TodoItems", ConnectionStringSetting = "CosmosDBConnectionString")] DocumentClient client, string id,
           ILogger log)
        {
            var currentUser = GetCurrentUserName(req, log);
            log.LogInformation("Deleting document with ID {id} for user {currentUser.UniqueName}");

            Uri documentUri = UriFactory.CreateDocumentUri("ServerlessTodo", "TodoItems", id);

            try
            {
                var item = await client.ReadDocumentAsync<TodoItem>(documentUri);

                // Verify the user owns the document and can delete it
                if (item.Document.ItemOwner == currentUser.UniqueName)
                {
                    await client.DeleteDocumentAsync(documentUri);
                }
                else
                {
                    log.LogWarning($"Document with ID: {id} does not belong to user {currentUser.UniqueName}");
                }
            }
            catch (DocumentClientException ex)
            {
                if (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Document does not exist or was already deleted
                    log.LogWarning($"Document with ID: {id} not found.");
                }
                else
                {
                    // Something else happened
                    throw ex;
                }
            }

            return new NoContentResult();
        }
    }
}
