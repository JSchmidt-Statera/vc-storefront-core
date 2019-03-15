using Microsoft.AspNetCore.Mvc;
using PagedList.Core;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VirtoCommerce.Storefront.Infrastructure;
using VirtoCommerce.Storefront.Model;
using VirtoCommerce.Storefront.Model.Cart.Services;
using VirtoCommerce.Storefront.Model.Catalog;
using VirtoCommerce.Storefront.Model.Common;
using VirtoCommerce.Storefront.Model.Common.Exceptions;
using VirtoCommerce.Storefront.Model.Quote;
using VirtoCommerce.Storefront.Model.Quote.Services;
using VirtoCommerce.Storefront.Model.Services;

using System.Net.Http.Headers;
using System.Text;
using System.Net.Http;
using System.Web;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;


namespace VirtoCommerce.Storefront.Controllers.Api
{
    [StorefrontApiRoute("")]
    public class ApiQuoteRequestController : StorefrontControllerBase
    {
        private readonly IQuoteRequestBuilder _quoteRequestBuilder;
        private readonly ICartBuilder _cartBuilder;
        private readonly ICatalogService _catalogService;
        private readonly IQuoteService _quoteService;

        public ApiQuoteRequestController(IWorkContextAccessor workContextAccessor, IStorefrontUrlBuilder urlBuilder, ICartBuilder cartBuilder, IQuoteRequestBuilder quoteRequestBuilder, ICatalogService catalogService, IQuoteService quoteService)
            : base(workContextAccessor, urlBuilder)
        {
            _quoteRequestBuilder = quoteRequestBuilder;
            _cartBuilder = cartBuilder;
            _catalogService = catalogService;
            _quoteService = quoteService;
        }

        // POST: storefrontapi/quoterequests/search
        [HttpPost("quoterequests/search")]
        public ActionResult QuoteSearch([FromBody] QuoteSearchCriteria criteria)
        {
            if (WorkContext.CurrentUser.IsRegisteredUser)
            {
                //allow search only within self quotes
                criteria.CustomerId = WorkContext.CurrentUser.Id;
                var result = _quoteService.SearchQuotes(criteria);

                SyncCpqStatus(result.AsEnumerable<QuoteRequest>()).Wait();

                return Json(new
                {
                    Results = result,
                    TotalCount = result.TotalItemCount
                });
            }
            return NoContent();
        }

        private async Task SyncCpqStatus(IEnumerable<QuoteRequest> results)
        {
            var client = new System.Net.Http.HttpClient();
            var endPoint = "https://prod-20.westus.logic.azure.com:443";
            var resource = "/workflows/ae2e0b8ee76f48efb4ce51c99c84ff65/triggers/manual/paths/invoke";
            var parameters = "?api-version=2016-10-01&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=0b0N_oaqS98_zKZqAYJJ5UjxT1BhUVucYu2v2rfSwJQ";
            var uri = endPoint + resource + parameters;

            foreach (var result in results)
            {
                var vcQuoteNumber = result.Number;
                var vcQuoteStatus = result.Status;
                var cpqQuoteStatus = string.Empty;

                byte[] byteData = System.Text.Encoding.UTF8.GetBytes(@"{""vcQuoteNumber"": """ + vcQuoteNumber + "\"}");

                using (var content = new System.Net.Http.ByteArrayContent(byteData))
                {
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    var response = await client.PostAsync(uri, content);

                    ProcessCpqQuoteJson(response.Headers);
                    cpqQuoteStatus = response.Headers.GetValues("cpqStatus").First();
                }

                if (vcQuoteStatus != cpqQuoteStatus)
                {
                    result.Status = cpqQuoteStatus;
                    await _quoteRequestBuilder.LoadQuoteRequestAsync(vcQuoteNumber, WorkContext.CurrentLanguage, WorkContext.CurrentCurrency);
                    EnsureQuoteRequestBelongsToCurrentCustomer(_quoteRequestBuilder.QuoteRequest);

                    using (await AsyncLock.GetLockByKey(_quoteRequestBuilder.QuoteRequest.Id).LockAsync())
                    {
                        _quoteRequestBuilder.QuoteRequest.Status = cpqQuoteStatus;
                        await _quoteRequestBuilder.SaveAsync();
                    }
                }
            }
        }

        // GET: storefrontapi/quoterequests/{number}/itemscount
        [HttpGet("quoterequests/{number}/itemscount")]
        public async Task<ActionResult> GetItemsCount(string number)
        {
            await _quoteRequestBuilder.LoadQuoteRequestAsync(number, WorkContext.CurrentLanguage, WorkContext.CurrentCurrency);

            var quoteRequest = _quoteRequestBuilder.QuoteRequest;
            EnsureQuoteRequestBelongsToCurrentCustomer(quoteRequest);

            return Json(new { Id = quoteRequest.Id, ItemsCount = quoteRequest.ItemsCount });
        }

        // GET: storefrontapi/quoterequests/{number}
        [HttpGet("quoterequests/{number}")]
        public async Task<ActionResult> Get(string number)
        {
            var builder = await _quoteRequestBuilder.LoadQuoteRequestAsync(number, WorkContext.CurrentLanguage, WorkContext.CurrentCurrency);
            var quoteRequest = builder.QuoteRequest;

            EnsureQuoteRequestBelongsToCurrentCustomer(quoteRequest);

            quoteRequest.Customer = WorkContext.CurrentUser;

            return Json(quoteRequest);
        }

        // GET: storefrontapi/quoterequest/current
        [HttpGet("quoterequest/current")]
        public ActionResult GetCurrent()
        {
            EnsureQuoteRequestBelongsToCurrentCustomer(WorkContext.CurrentQuoteRequest.Value);
            return Json(WorkContext.CurrentQuoteRequest.Value);
        }

        // POST: storefrontapi/quoterequests/current/items
        [HttpPost("quoterequests/current/items")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> AddItem([FromBody] AddQuoteItem addQuoteItem)
        {
            EnsureQuoteRequestBelongsToCurrentCustomer(WorkContext.CurrentQuoteRequest.Value);
            _quoteRequestBuilder.TakeQuoteRequest(WorkContext.CurrentQuoteRequest.Value);

            using (await AsyncLock.GetLockByKey(GetAsyncLockQuoteKey(_quoteRequestBuilder.QuoteRequest.Id)).LockAsync())
            {
                var products = await _catalogService.GetProductsAsync(new[] { addQuoteItem.ProductId }, ItemResponseGroup.ItemInfo | ItemResponseGroup.ItemWithPrices);
                if (products != null && products.Any())
                {
                    _quoteRequestBuilder.AddItem(products.First(), addQuoteItem.Quantity);
                    await _quoteRequestBuilder.SaveAsync();
                }
            }

            return Ok();
        }

        // DELETE: storefrontapi/quoterequest/{number}/items/{itemId}
        [HttpDelete("quoterequests/{number}/items/{itemId}")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> RemoveItem(string number, string itemId)
        {
            await _quoteRequestBuilder.LoadQuoteRequestAsync(number, WorkContext.CurrentLanguage, WorkContext.CurrentCurrency);

            using (await AsyncLock.GetLockByKey(GetAsyncLockQuoteKey(_quoteRequestBuilder.QuoteRequest.Id)).LockAsync())
            {
                _quoteRequestBuilder.RemoveItem(itemId);
                await _quoteRequestBuilder.SaveAsync();
            }
            return Ok();
        }

        // POST: storefrontapi/quoterequest/{number}/submit
        [HttpPost("quoterequests/{number}/submit")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Submit(string number, [FromBody] QuoteRequestFormModel quoteForm)
        {
            await _quoteRequestBuilder.LoadQuoteRequestAsync(number, WorkContext.CurrentLanguage, WorkContext.CurrentCurrency);

            EnsureQuoteRequestBelongsToCurrentCustomer(_quoteRequestBuilder.QuoteRequest);

            using (await AsyncLock.GetLockByKey(WorkContext.CurrentQuoteRequest.Value.Id).LockAsync())
            {
                _quoteRequestBuilder.Update(quoteForm).Submit();
                await _quoteRequestBuilder.SaveAsync();
            }

            CreateCpqQuote(_quoteRequestBuilder.QuoteRequest.Number);

            return Ok();
        }

        private async void CreateCpqQuote(string quoteName)
        {
            var client = new System.Net.Http.HttpClient();
            var queryString = System.Web.HttpUtility.ParseQueryString(string.Empty);
            var endPoint = "https://prod-11.westus.logic.azure.com:443";
            var resource = "/workflows/915a6f97b54a4099a96855db2dcc862f/triggers/manual/paths/invoke";
            var parameters = "?api-version=2016-10-01&sp=/triggers/manual/run&sv=1.0&sig=ir9firMdH8eoQIIQfppuhNVW9c2lxjpYWV8fIw8rKBY";
            var uri = endPoint + resource + parameters;

            _quoteRequestBuilder.QuoteRequest.EmployeeName = this.WorkContext.CurrentUser.Contact.FullName;
            string vcQuote = Newtonsoft.Json.JsonConvert.SerializeObject(_quoteRequestBuilder.QuoteRequest);
            byte[] byteData = System.Text.Encoding.UTF8.GetBytes(vcQuote);

            using (var content = new System.Net.Http.ByteArrayContent(byteData))
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                var response = await client.PostAsync(uri, content);

                ProcessCpqQuoteJson(response.Headers);
            }
        }

        private void ProcessCpqQuoteJson(HttpResponseHeaders headers)
        {
            string cpqQuoteNumberKey = "cpqQuoteNumber";
            string cpqQuoteNumber = string.Empty;

            if (headers.Contains(cpqQuoteNumberKey))
                cpqQuoteNumber = headers.GetValues(cpqQuoteNumberKey).First();

        }

        // POST: storefrontapi/quoterequest/{number}/reject
        [HttpPost("quoterequests/{number}/reject")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Reject(string number)
        {
            await _quoteRequestBuilder.LoadQuoteRequestAsync(number, WorkContext.CurrentLanguage, WorkContext.CurrentCurrency);

            EnsureQuoteRequestBelongsToCurrentCustomer(_quoteRequestBuilder.QuoteRequest);

            using (await AsyncLock.GetLockByKey(_quoteRequestBuilder.QuoteRequest.Id).LockAsync())
            {
                _quoteRequestBuilder.Reject();
                await _quoteRequestBuilder.SaveAsync();
            }
            return Ok();
        }

        // PUT: storefrontapi/quoterequest/{number}/update
        [HttpPut("quoterequests/{number}")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Update(string number, [FromBody] QuoteRequestFormModel quoteRequest)
        {
            await _quoteRequestBuilder.LoadQuoteRequestAsync(number, WorkContext.CurrentLanguage, WorkContext.CurrentCurrency);

            EnsureQuoteRequestBelongsToCurrentCustomer(_quoteRequestBuilder.QuoteRequest);

            using (await AsyncLock.GetLockByKey(_quoteRequestBuilder.QuoteRequest.Id).LockAsync())
            {
                _quoteRequestBuilder.Update(quoteRequest);
                await _quoteRequestBuilder.SaveAsync();
            }

            return Ok();
        }

        // POST: storefrontapi/quoterequests/{number}/totals
        [HttpPost("quoterequests/{number}/totals")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> CalculateTotals(string number, [FromBody] QuoteRequestFormModel quoteRequest)
        {
            await _quoteRequestBuilder.LoadQuoteRequestAsync(number, WorkContext.CurrentLanguage, WorkContext.CurrentCurrency);

            EnsureQuoteRequestBelongsToCurrentCustomer(_quoteRequestBuilder.QuoteRequest);

            //Apply user changes without saving
            _quoteRequestBuilder.Update(quoteRequest);
            await _quoteRequestBuilder.CalculateTotalsAsync();

            return Json(_quoteRequestBuilder.QuoteRequest.Totals);
        }

        // POST: storefrontapi/quoterequests/{number}/confirm
        [HttpPost("quoterequests/{number}/confirm")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Confirm([FromRoute]string number, [FromBody] QuoteRequestFormModel quoteRequest)
        {
            await _quoteRequestBuilder.LoadQuoteRequestAsync(number, WorkContext.CurrentLanguage, WorkContext.CurrentCurrency);

            EnsureQuoteRequestBelongsToCurrentCustomer(_quoteRequestBuilder.QuoteRequest);

            _quoteRequestBuilder.Update(quoteRequest).Confirm();
            await _quoteRequestBuilder.SaveAsync();

            await _cartBuilder.TakeCartAsync(WorkContext.CurrentCart.Value);
            await _cartBuilder.FillFromQuoteRequestAsync(_quoteRequestBuilder.QuoteRequest);
            await _cartBuilder.SaveAsync();

            return Ok();
        }

        private static string GetAsyncLockQuoteKey(string quoteId)
        {
            return "quote-request:" + quoteId;
        }

        private void EnsureQuoteRequestBelongsToCurrentCustomer(QuoteRequest quote)
        {
            if (WorkContext.CurrentUser.Id != quote.CustomerId)
            {
                throw new StorefrontException("Requested quote not belongs to user " + WorkContext.CurrentUser.UserName);
            }
        }
    }
}
