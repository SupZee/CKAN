using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Autofac;
using ChinhDo.Transactions.FileManager;
using CKAN.Configuration;
using CurlSharp;
using log4net;

namespace CKAN
{
    /// <summary>
    ///     Doing something with the network? Do it here.
    /// </summary>

    public class Net
    {
        // The user agent that we report to web sites
        public static string UserAgentString = "Mozilla/4.0 (compatible; CKAN)";

        private static readonly ILog          log             = LogManager.GetLogger(typeof(Net));

        public static readonly Dictionary<string, Uri> ThrottledHosts = new Dictionary<string, Uri>()
        {
            {
                "api.github.com",
                new Uri("https://github.com/KSP-CKAN/CKAN/wiki/Adding-a-GitHub-API-authtoken")
            }
        };

        /// <summary>
        /// Make a HEAD request to get the ETag of a URL without downloading it
        /// </summary>
        /// <param name="url">Remote URL to check</param>
        /// <returns>
        /// ETag value of the URL if any, otherwise null, see
        /// https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/ETag
        /// </returns>
        public static string CurrentETag(Uri url)
        {
            WebRequest req = WebRequest.Create(url);
            req.Method = "HEAD";
            HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
            string val = resp.Headers["ETag"]?.Replace("\"", "");
            resp.Close();
            return val;
        }

        /// <summary>
        /// Downloads the specified url, and stores it in the filename given.
        /// If no filename is supplied, a temporary file will be generated.
        /// Returns the filename the file was saved to on success.
        /// Throws an exception on failure.
        /// Throws a MissingCertificateException *and* prints a message to the
        /// console if we detect missing certificates (common on a fresh Linux/mono install)
        /// </summary>
        public static string Download(Uri url, out string etag, string filename = null, IUser user = null)
        {
            return Download(url.OriginalString, out etag, filename, user);
        }

        public static string Download(Uri url, string filename = null, IUser user = null)
        {
            string etag;
            return Download(url, out etag, filename, user);
        }

        public static string Download(string url, string filename = null, IUser user = null)
        {
            string etag;
            return Download(url, out etag, filename, user);
        }

        public static string Download(string url, out string etag, string filename = null, IUser user = null)
        {
            TxFileManager FileTransaction = new TxFileManager();

            user = user ?? new NullUser();
            user.RaiseMessage("Downloading {0}", url);

            // Generate a temporary file if none is provided.
            if (filename == null)
            {
                filename = FileTransaction.GetTempFileName();
            }

            log.DebugFormat("Downloading {0} to {1}", url, filename);

            try
            {
                var agent = MakeDefaultHttpClient();
                agent.DownloadFile(url, filename);
                etag = agent.ResponseHeaders.Get("ETag")?.Replace("\"", "");
            }
            catch (Exception exc)
            {
                var wexc = exc as WebException;
                if (wexc?.Status == WebExceptionStatus.ProtocolError)
                {
                    // Get redirect if redirected.
                    // This is needed when redirecting from HTTPS to HTTP on .NET Core.
                    var response = wexc.Response as HttpWebResponse;
                    if (response?.StatusCode == HttpStatusCode.Redirect)
                    {
                        return Download(response.GetResponseHeader("Location"), out etag, filename, user);
                    }
                    // Otherwise it's a valid failure from the server (probably a 404), keep it
                }
                else
                {
                    // Something other than the server failed, retry with Curl
                    log.InfoFormat("Native file download failed, trying with CurlSharp...");
                    etag = null;

                    try
                    {
                        Curl.Init();

                        using (FileStream stream = File.OpenWrite(filename))
                        {
                            string header  = string.Empty;

                            var client = Curl.CreateEasy(url, null, stream, delegate(byte[] buf, int size, int nmemb, object extraData)
                            {
                                header += Encoding.UTF8.GetString(buf);
                                return size * nmemb;
                            });

                            using (client)
                            {
                                var result = client.Perform();
                                var returnCode = client.ResponseCode;

                                if (result != CurlCode.Ok || returnCode >= 300)
                                {
                                    // Always log if it's an error.
                                    log.ErrorFormat("Response from {0}:\r\n\r\n{1}\r\n{2}", url, header, "Content not logged because it is likely a file.");

                                    WebException curlException =
                                        new WebException($"Curl download failed with status {returnCode}.");
                                    throw new NativeAndCurlDownloadFailedKraken(
                                        new List<Exception> {exc, curlException},
                                        url.ToString(), header, null, returnCode
                                    );
                                }
                                else
                                {
                                    // Only log if debug flag is set.
                                    log.DebugFormat("Response from {0}:\r\n\r\n{1}\r\n{2}", url, header, "Content not logged because it is likely a file.");
                                }
                            }
                        }

                        Curl.CleanUp();
                        return filename;
                    }
                    catch
                    {
                        // D'oh, failed again. Fall through to clean-up handling.
                    }

                }

                // Clean up our file, it's unlikely to be complete.
                // We do this even though we're using transactional files, as we may not be in a transaction.
                // It's okay if this fails.
                try
                {
                    log.DebugFormat("Removing {0} after web error failure", filename);
                    FileTransaction.Delete(filename);
                }
                catch
                {
                    // Apparently we need a catch, even if we do nothing.
                }

                // Look for an exception regarding the authentication.
                if (Regex.IsMatch(exc.ToString(), "The authentication or decryption has failed."))
                {
                    throw new MissingCertificateKraken("Failed downloading " + url, exc);
                }

                // Not the exception we were looking for! Throw it further upwards!
                throw;
            }

            return filename;
        }

        public class DownloadTarget
        {
            public Uri    url         { get; private set; }
            public Uri    fallbackUrl { get; private set; }
            public string filename    { get; private set; }
            public long   size        { get; private set; }
            public string mimeType    { get; private set; }

            public DownloadTarget(Uri url, Uri fallback = null, string filename = null, long size = 0, string mimeType = "")
            {
                TxFileManager FileTransaction = new TxFileManager();

                this.url         = url;
                this.fallbackUrl = fallback;
                this.filename    = string.IsNullOrEmpty(filename)
                    ? FileTransaction.GetTempFileName()
                    : filename;
                this.size        = size;
                this.mimeType    = mimeType;
            }
        }

        public static string DownloadWithProgress(string url, string filename = null, IUser user = null)
        {
            return DownloadWithProgress(new Uri(url), filename, user);
        }

        public static string DownloadWithProgress(Uri url, string filename = null, IUser user = null)
        {
            var targets = new[] {
                new DownloadTarget(url, null, filename)
            };
            DownloadWithProgress(targets, user);
            return targets.First().filename;
        }

        public static void DownloadWithProgress(ICollection<DownloadTarget> downloadTargets, IUser user = null)
        {
            new NetAsyncDownloader(user ?? new NullUser())
            {
                onOneCompleted = (url, filename, error) =>
                {
                    if (error != null)
                    {
                        user?.RaiseError(error.ToString());
                    }
                    else
                    {
                        File.Move(filename, downloadTargets.First(p => p.url == url).filename);
                    }
                }
            }.DownloadAndWait(downloadTargets);
        }

        public static string DownloadText(Uri url, string authToken = "", string mimeType = null)
        {
            log.DebugFormat("About to download {0}", url.OriginalString);

            try
            {
                WebClient agent = MakeDefaultHttpClient();

                // Check whether to use an auth token for this host
                if (!string.IsNullOrEmpty(authToken)
                    || (ServiceLocator.Container.Resolve<IConfiguration>().TryGetAuthToken(url.Host, out authToken)
                        && !string.IsNullOrEmpty(authToken)))
                {
                    log.InfoFormat("Using auth token for {0}", url.Host);
                    // Send our auth token to the GitHub API (or whoever else needs one)
                    agent.Headers.Add("Authorization", $"token {authToken}");
                }
                if (!string.IsNullOrEmpty(mimeType))
                {
                    log.InfoFormat("Setting MIME type {0}", mimeType);
                    agent.Headers.Add("Accept", mimeType);
                }

                string content = agent.DownloadString(url.OriginalString);
                string header  = agent.ResponseHeaders.ToString();

                log.DebugFormat("Response from {0}:\r\n\r\n{1}\r\n{2}", url, header, content);

                return content;
            }
            catch (Exception exc)
            {
                if ((exc as WebException)?.Status == WebExceptionStatus.ProtocolError)
                {
                    // Don't retry with Curl if we get a valid failure from the server
                    throw;
                }
                
                log.InfoFormat("Native text download failed, trying with CurlSharp...");

                string content = string.Empty;
                string header  = string.Empty;

                var client = Curl.CreateEasy(url.OriginalString, authToken,
                    delegate (byte[] buf, int size, int nmemb, object extraData)
                    {
                        content += Encoding.UTF8.GetString(buf);
                        return size * nmemb;
                    },
                    delegate(byte[] buf, int size, int nmemb, object extraData)
                    {
                        header += Encoding.UTF8.GetString(buf);
                        return size * nmemb;
                    }
                );

                using (client)
                {
                    var result = client.Perform();
                    var returnCode = client.ResponseCode;

                    if (result != CurlCode.Ok || returnCode >= 300 )
                    {
                        // Always log if it's an error.
                        log.ErrorFormat("Response from {0}:\r\n\r\n{1}\r\n{2}", url, header, content);

                        WebException curlException = new WebException($"Curl download failed with status {returnCode}.");
                        throw new NativeAndCurlDownloadFailedKraken(
                            new List<Exception> {exc, curlException},
                            url.ToString(), header, content, returnCode
                        );
                    }
                    else
                    {
                        // Only log if debug flag is set
                        log.DebugFormat("Response from {0}:\r\n\r\n{1}\r\n{2}", url, header, content);
                        return content;
                    }
                }
            }
        }

        public static Uri ResolveRedirect(Uri uri)
        {
            const int maximumRequest = 5;

            var currentUri = uri;
            for (var i = 0; i < maximumRequest; i++)
            {
                var client = new RedirectWebClient();

                // The empty using is so that we dispose of the stream and don't block
                // We don't ACTUALLY attempt to download the file, but it appears that if the client sees a
                // Content Length in the response it thinks there will be a response body and blocks.
                using (client.OpenRead(currentUri)) { }

                var location = client.ResponseHeaders["Location"];

                if (location == null)
                {
                    return currentUri;
                }
                else
                {
                    log.InfoFormat("{0} redirected to {1}", currentUri, location);

                    if (Uri.IsWellFormedUriString(location, UriKind.Absolute))
                    {
                        currentUri = new Uri(location);
                    }
                    else if (Uri.IsWellFormedUriString(location, UriKind.Relative))
                    {
                        currentUri = new Uri(currentUri, location);
                        log.DebugFormat("Relative URL {0} is absolute URL {1}", location, currentUri);
                    }
                    else
                    {
                        throw new Kraken("Invalid URL in Location header: " + location);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Translate a URL into a form that returns the raw contents of a file
        /// Only changes GitHub URLs, others are left as-is
        /// </summary>
        /// <param name="remoteUri">URL to handle</param>
        /// <returns>
        /// URL pointing to the raw contents of the input URL
        /// </returns>
        public static Uri GetRawUri(Uri remoteUri)
        {
            // Authors may use the URI of the GitHub file page instead of the URL to the actual raw file.
            // Detect that case and automatically transform the remote URL to one we can use.
            // This is hacky and fragile but it's basically what KSP-AVC itself does in its
            // FormatCompatibleUrl(string) method so we have to go along with the flow:
            // https://github.com/CYBUTEK/KSPAddonVersionChecker/blob/ff94000144a666c8ff637c71b802e1baee9c15cd/KSP-AVC/AddonInfo.cs#L199
            // However, this implementation is more robust as it actually parses the URI rather than doing
            // basic string replacements.
            if (string.Compare(remoteUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) == 0)
            {
                // We expect a non-raw URI to be in one of two forms:
                //  1. https://github.com/<USER>/<PROJECT>/blob/<BRANCH>/<PATH>
                //  2. https://github.com/<USER>/<PROJECT>/tree/<BRANCH>/<PATH>
                //
                // Therefore, we expect at least six segments in the path:
                //  1. "/"
                //  2. "<USER>/"
                //  3. "<PROJECT>/"
                //  4. "blob/" or "tree/"
                //  5. "<BRANCH>/"
                //  6+. "<PATH>"
                //
                // And that the fourth segment (index 3) is either "blob/" or "tree/"

                var remoteUriBuilder = new UriBuilder(remoteUri)
                {
                    // Replace host with raw host
                    Host = "raw.githubusercontent.com"
                };

                // Check that the path is what we expect
                var segments = remoteUri.Segments.ToList();

                if (segments.Count >= 4
                    && string.Compare(segments[3], "raw/", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    log.InfoFormat("Remote GitHub URL is in raw format, using as is.");
                    return remoteUri;
                }
                if (segments.Count < 6 ||
                    string.Compare(segments[3], "blob/", StringComparison.OrdinalIgnoreCase) != 0 &&
                    string.Compare(segments[3], "tree/", StringComparison.OrdinalIgnoreCase) != 0)
                {
                    log.WarnFormat("Remote non-raw GitHub URL is in an unknown format, using as is.");
                    return remoteUri;
                }

                // Remove "blob/" or "tree/" segment from raw URI
                segments.RemoveAt(3);
                remoteUriBuilder.Path = string.Join("", segments);

                log.InfoFormat("Canonicalized non-raw GitHub URL to: {0}", remoteUriBuilder.Uri);

                return remoteUriBuilder.Uri;
            }
            else
            {
                return remoteUri;
            }
        }

        private static WebClient MakeDefaultHttpClient()
        {
            var client = new WebClient();
            client.Headers.Add("User-Agent", UserAgentString);
            return client;
        }

        // HACK: The ancient WebClient doesn't support setting the request type to HEAD and WebRequest doesn't support
        // setting the User-Agent header.
        // Maybe one day we'll be able to use HttpClient (https://msdn.microsoft.com/en-us/library/system.net.http.httpclient%28v=vs.118%29.aspx)
        private sealed class RedirectWebClient : WebClient
        {
            public RedirectWebClient()
            {
                Headers.Add("User-Agent", UserAgentString);
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                var webRequest = (HttpWebRequest)base.GetWebRequest(address);
                webRequest.AllowAutoRedirect = false;

                webRequest.Method = "HEAD";

                return webRequest;
            }
        }
    }
}
