﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Toggl.Models;
using Toggl.Services;

namespace Toggl
{
    /// <summary>
    /// Communicates with the Toggl API
    /// </summary>
    public partial class TogglClient : IDisposable
    {
        #region Services

        /// <summary>
        /// Client service
        /// </summary>
        public ClientService Clients { get; }
        /// <summary>
        /// Time entry service
        /// </summary>
        public TimeEntryService TimeEntries { get; }
        /// <summary>
        /// Project service
        /// </summary>
        public ProjectService Projects { get; }
        /// <summary>
        /// Tag service
        /// </summary>
        public TagService Tags { get; }
        /// <summary>
        /// Task service
        /// </summary>
        public TaskService Tasks { get; }
        /// <summary>
        /// Workspace service
        /// </summary>
        public WorkspaceService Workspaces { get; }

        #endregion

        const string BaseUrl = "https://www.toggl.com/api/v9/";

        private readonly JsonSerializer _jsonSerializer;
        private readonly HttpClient _httpClient;

        /// <summary>
        /// User-Agent header information to include in requests. Default is null.
        /// If null, User-Agent header will not be included in requests.
        /// </summary>
        public ProductInfoHeaderValue UserAgent { get; set; }

        /// <summary>
        /// Creates new <see cref="TogglClient"/>
        /// </summary>
        /// <param name="apiToken">Toggl API token</param>
        public TogglClient(string apiToken)
        {
            Clients = new ClientService(this);
            TimeEntries = new TimeEntryService(this);
            Projects = new ProjectService(this);
            Tags = new TagService(this);
            Tasks = new TaskService(this);
            Workspaces = new WorkspaceService(this);

            _jsonSerializer = JsonSerializer.CreateDefault();

            _httpClient = new HttpClient();
            _httpClient.BaseAddress = new Uri(BaseUrl);
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (UserAgent != null)
                _httpClient.DefaultRequestHeaders.UserAgent.Add(UserAgent);

            // authorization (basic)
            var authorizationParameter = Convert.ToBase64String(Encoding.GetEncoding("ascii").GetBytes($"{apiToken}:api_token"));
            var header = new AuthenticationHeaderValue("Basic", authorizationParameter);
            _httpClient.DefaultRequestHeaders.Authorization = header;
        }

        /// <summary>
        /// Releases all resources.
        /// </summary>
        public void Dispose()
        {
            _httpClient.Dispose();
        }

        #region Helpers

        internal async Task<T> Get<T>(string uri, object model = null)
            => await RequestAsync<T>(HttpMethod.Get, uri, model);

        internal async Task<T> Post<T>(string uri, T model)
            => await RequestAsync<T>(HttpMethod.Post, uri, model);

        internal async Task<T> Put<T>(string uri, object model)
            => await RequestAsync<T>(HttpMethod.Put, uri, model);

        protected virtual StringContent GetJsonContent(string json)
            => new StringContent(json, Encoding.UTF8, "application/json");

        /// <summary>
        /// Reads through all or specific pages returned by a given query.
        /// </summary>
        /// <typeparam name="T">Type of item returned by query</typeparam>
        /// <param name="pagedQuery">Query delegate function</param>
        /// <param name="fromPage">First page to read</param>
        /// <param name="toPage">Last page to read (inclusive)</param>
        /// <returns>All items in the specified page range</returns>
        /// <example>
        /// <code>
        /// var result = await togglClient.GetPages(page => togglClient.Tasks.ListAsync(workspaceId: 1234567, page: page));
        /// </code>
        /// </example>
        public virtual async Task<List<T>> GetPages<T>(Func<int, Task<PagedResult<T>>> pagedQuery, int fromPage = 1, int toPage = int.MaxValue)
        {
            if (pagedQuery == null) throw new ArgumentNullException(nameof(pagedQuery));
            Utilities.CheckPageArgument(fromPage);
            Utilities.CheckPageArgument(toPage);
            if (toPage < fromPage) throw new ArgumentException($"Invalid page range specified: {nameof(toPage)} must be larger than or equal to {nameof(fromPage)}", nameof(toPage));

            bool more;
            List<T> items = new List<T>();
            int page = fromPage;

            do
            {
                // TODO: rate limiting hack... Needs more elegant fix
                if (page > fromPage) await System.Threading.Tasks.Task.Delay(500);

                // Get next page
                var result = await pagedQuery(page);
                if (result.Items != null) items.AddRange(result.Items);
                // See if there are more items to retrieve
                more = result.TotalCount > result.Page * result.ItemsPerPage;
                page++;
            }
            while (more && page <= toPage);

            return items;
        }

        internal virtual async Task<T> RequestAsync<T>(HttpMethod method, string uri, object model = null)
        {
            var request = new HttpRequestMessage(method, uri);
            if (model != null)
            {
                var requestBody = JsonConvert.SerializeObject(model);
                request.Content = GetJsonContent(requestBody);
            }
            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new TogglApiException(responseBody, response);
            }
            var result = JsonConvert.DeserializeObject<T>(responseBody);
            return result;
        }

        /// <summary>
        /// Compares current item and a snapshot of its previous state to supply the most minimal payload (model) when performing updates
        /// where only changed fields are necessary to include in request.
        /// </summary>
        /// <typeparam name="T">Type of item being updated</typeparam>
        /// <param name="current">Item with current property values</param>
        /// <param name="previous">A copy of the item with property values before changes were made</param>
        /// <param name="changed">Returns a value indicating whether any changes were found on item based on comparison</param>
        /// <param name="skipProperties">Specifies properties of <typeparamref name="T"/> that should be ignored completely</param>
        /// <returns>
        /// If <paramref name="previous"/> is not null, returns a <see cref="JObject"/> that contains the payload necessary to perform the update.
        /// Otherwise, this method simply returns <paramref name="current"/>.
        /// </returns>
        /// <remarks>
        /// Properties marked with <see cref="JsonIgnoreAttribute"/> are ignored.
        /// Property names are extracted from any <see cref="JsonPropertyAttribute"/>s, if present. Otherwise uses actual property names in model.
        /// </remarks>
        internal virtual object GetMinimalModelForUpdate<T>(T current, T previous, out bool changed, string[] skipProperties = null)
            where T : IBaseModel
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (previous == null)
            {
                // No previous state to compare to, so cannot create minimal model. Instead return full model.
                changed = true; // Assume there are changes to save, since we can't tell.
                return current;
            }

            var properties = typeof(T).GetTypeInfo().DeclaredProperties;
            var model = new JObject();
            changed = false;

            // always include ID
            model.Add("id", current.Id);

            foreach (var prop in properties)
            {
                if (prop.Name == nameof(IBaseModel.Id)) continue; // skip Id (it's added above)
                var attributes = prop.GetCustomAttributes();
                if (attributes.OfType<JsonIgnoreAttribute>().Any()) continue; // don't check or serialize ignored attributes
                if (skipProperties != null && skipProperties.Any(p => p == prop.Name)) continue; // skip specified properties

                // See if property value was changed by comparing current value to previous
                var value = prop.GetValue(current);
                if (!Equals(prop.GetValue(previous), value))
                {
                    // Property value has changed, and should be included in payload.
                    var attr = attributes.OfType<JsonPropertyAttribute>().FirstOrDefault();
                    // Use the correct property name; that is, if JsonPropertyAttribute is present, use that name.
                    // Otherwise fall back to property name.
                    string propertyName = (attr != null && !string.IsNullOrEmpty(attr.PropertyName))
                        ? attr.PropertyName
                        : prop.Name;

                    // Add property to model
                    model.Add(propertyName, JToken.FromObject(value));
                    changed = true;
                }
            }
            return model;
        }

        #endregion
    }
}
