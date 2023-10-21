using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;
using Garmin.Connect.Converters;
using Garmin.Connect.Exceptions;
using Garmin.Connect.Models;

namespace Garmin.Connect;

public class GarminConnectContext
{
    private readonly HttpClient _httpClient;
    private readonly IAuthParameters _authParameters;
    private OAuth2Token _oAuth2Token;

    private const int Attempts = 3;
    private const int DelayAfterFailAuth = 300;
    private readonly Regex _csrfRegex = new Regex(@"name=""_csrf""\s+value=""(\w+)""", RegexOptions.Compiled);
    private readonly Regex _responseUrlRegex = new Regex(@"""(https:[^""]+?ticket=[^""]+)""", RegexOptions.Compiled);
    private string _tokenCached;
    private readonly GarminAuthenticationService _garminAuthenticationService;

    public GarminConnectContext(HttpClient httpClient, IAuthParameters authParameters)
    {
        _httpClient = httpClient;
        _authParameters = authParameters;
        _garminAuthenticationService = new GarminAuthenticationService(_httpClient, authParameters);
    }

    public async Task ReLoginIfExpired(bool force = false)
    {
        if (force || _oAuth2Token is null)
        {
            var oAuth2Token = await _garminAuthenticationService.RefreshGarminAuthenticationAsync();

            _oAuth2Token = oAuth2Token;
        }
    }

    internal GarminSocialProfile Profile { get; set; }

    public async Task<T> GetAndDeserialize<T>(string url)
    {
        var response = await MakeHttpGet(url);
        var json = await response.Content.ReadAsByteArrayAsync();

        // Console.WriteLine($"{url}\n{json}\n\n\n");
        // return default;

        if (json.Length == 0)
            return default(T);

        return GarminSerializer.To<T>(json);
    }

    public Task<HttpResponseMessage> MakeHttpGet(string url) =>
        MakeHttpRequest(url, HttpMethod.Get);

    public Task<HttpResponseMessage> MakeHttpPut<TBody>(string url, TBody body) =>
        MakeHttpRequest(url, HttpMethod.Put, JsonContent.Create(body));

    private async Task<HttpResponseMessage> MakeHttpRequest(string url, HttpMethod method, HttpContent content = null)
    {
        var force = false;
        Exception exception = null;

        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                await ReLoginIfExpired(force);

                var requestUri = new Uri($"{_authParameters.BaseUrl}{url}");
                var httpRequestMessage = new HttpRequestMessage(method, requestUri);
                httpRequestMessage.Headers.Add("cookie", _authParameters.Cookies);
                httpRequestMessage.Headers.Add("authorization", $"Bearer {_oAuth2Token.Access_Token}");
                httpRequestMessage.Headers.Add("di-backend", "connectapi.garmin.com");
                httpRequestMessage.Content = content;

                var response = await _httpClient.SendAsync(httpRequestMessage);

                RaiseForStatus(response);

                return response;
            }
            catch (GarminConnectRequestException ex)
            {
                exception = ex;
                if (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    await Task.Delay(DelayAfterFailAuth);
                    force = true;
                    continue;
                }

                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        throw new GarminConnectAuthenticationException($"Authentication fail after {Attempts} attempts", exception);
    }

    private static void RaiseForStatus(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.TooManyRequests:
                throw new GarminConnectTooManyRequestsException();
            case HttpStatusCode.NoContent:
            case HttpStatusCode.OK:
                return;
            default:
            {
                var message = $"{response.RequestMessage?.Method.Method}: {response.RequestMessage?.RequestUri}";
                throw new GarminConnectRequestException(message, response.StatusCode);
            }
        }
    }

    private static TModel ParseJson<TModel>(string html, string key)
    {
        var dataRegex = new Regex($@"window\.{key} = (.*);", RegexOptions.Compiled);
        var dataMatch = dataRegex.Match(html);

        if (dataMatch.Success)
        {
            var json = dataMatch.Groups[1].Value.Replace("\\\"", "\"");
            var model = JsonSerializer.Deserialize<TModel>(json);
            if (model != null)
            {
                return model;
            }
        }

        throw new GarminConnectUnexpectedException(key);
    }
}