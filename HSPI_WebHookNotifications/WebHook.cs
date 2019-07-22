using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace HSPI_WebHookNotifications
{
    public class WebHook : IDisposable
    {
        public bool CheckServerCertificate {
            get => checkServerCertificate;
            set {
                checkServerCertificate = value;
                httpClientHandler.ServerCertificateCustomValidationCallback = checkServerCertificate
                    ? null
                    : HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
        }
        
        private readonly HttpClient httpClient;
        private readonly HttpClientHandler httpClientHandler;
        private readonly Uri uri;
        private bool checkServerCertificate = true;

        public WebHook(string uri) : this(new Uri(uri)) { }

        public WebHook(Uri uri) {
            this.uri = uri;

            httpClientHandler = new HttpClientHandler { UseCookies = true };
            if (uri.UserInfo.Length > 0) {
                httpClientHandler.PreAuthenticate = true;

                int colonIdx = uri.UserInfo.IndexOf(':');
                httpClientHandler.Credentials = colonIdx == -1
                    ? new NetworkCredential(uri.UserInfo, "")
                    : new NetworkCredential(uri.UserInfo.Substring(0, colonIdx), uri.UserInfo.Substring(colonIdx + 1));
            }

            httpClient = new HttpClient(httpClientHandler);
        }

        public Task<HttpResponseMessage> Execute(HttpContent content) {
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, uri) {
                Content = content
            };

            return httpClient.SendAsync(req);
        }

        public override string ToString() {
            return uri.ToString();
        }

        public void Dispose() {
            httpClient?.Dispose();
            httpClientHandler?.Dispose();
        }
    }
}
