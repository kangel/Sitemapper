using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;
using System.Web.Mvc;

namespace SiteMapper.Controllers
{
    public class SiteMapController : Controller
    {
        // GET: SiteMap
        public ActionResult Index(bool refresh = false)
        {
            bool isFileCached = false;
            bool.TryParse(System.Configuration.ConfigurationManager.AppSettings["IS_FILE_CACHED_SITE_MAP"], out isFileCached);

            string cacheFilePath = Server.MapPath("/Content/sitemap.xml");
            bool existsCacheFile = System.IO.File.Exists(cacheFilePath);

            bool needNoCalc = isFileCached && existsCacheFile && !refresh;

            if (needNoCalc)
            {
                return new FilePathResult(cacheFilePath, "text/xml");
            }
            else
            {
                //if i want to crawl the site
                string sitePrimaryUrl = System.Web.HttpContext.Current.Request.Url.OriginalString;

                //for main site without any action or controller below section will be skipped
                if (System.Web.HttpContext.Current.Request.Url.PathAndQuery != "/")
                    sitePrimaryUrl = sitePrimaryUrl.Replace(System.Web.HttpContext.Current.Request.Url.PathAndQuery, "/");

                UriBuilder uri = new UriBuilder(sitePrimaryUrl);

                var crawler = new Crawler()
                {
                    StartingUrl = sitePrimaryUrl,
                    PrimaryHost = uri.Host,
                };
                var vm = crawler.parseSite();
                if (isFileCached)
                {
                    ViewData.Model = vm;
                    using(var sw = new StringWriter())
                    {
                        var viewResult = ViewEngines.Engines.FindPartialView(ControllerContext, "Index");
                        var viewContext = new ViewContext(ControllerContext, viewResult.View, ViewData, TempData, sw);
                        viewResult.View.Render(viewContext, sw);
                        viewResult.ViewEngine.ReleaseView(ControllerContext, viewResult.View);
                        using (StreamWriter outfile = new StreamWriter(cacheFilePath))
                        {
                            outfile.Write(sw.ToString());
                        }
                        existsCacheFile = System.IO.File.Exists(cacheFilePath);
                    }
                    return new FilePathResult(cacheFilePath, "text/xml");
                }
                else
                {
                    return View(vm);
                }
            }
        }
    }

    class Crawler
    {
        public string StartingUrl { get; set; }

        public string PrimaryHost { get; set; }

        public Dictionary<string, CrawledItem> Results { get; set; }

        internal object parseSite()
        {
            Results = new Dictionary<string,CrawledItem>();

            crawlPage(StartingUrl, 10);

            var result = Results.Where(x => x.Value != null && x.Value.IsIncluded).ToArray().Select(x => new ParseResultItem(x));

            return result;
        }

        private void crawlPage(string url, int priority)
        {
            url = removeDefaultPortFromUrl(url);
            if (isCrawlerTarget(url))
            {
                if (!Results.ContainsKey(url))
                {
                    IEnumerable<string> references = new string[] { };

                    CrawledItem crawledItem = null;

                    using (var pageContent = ReadPage(url))
                    {
                        if (pageContent != null && pageContent.Document != null)
                        {
                            references = getReferences(pageContent.Document);
                            crawledItem = new CrawledItem(pageContent, priority);
                        }
                    }

                    Results.Add(url, crawledItem);

                    foreach (var reference in references)
                    {
                        crawlPage(reference, priority > 0 ? priority - 1 : 0);
                    }
                }
                else
                {
                    var prevResult = Results[url];
                    if (prevResult != null 
                        && !prevResult.IsDeclaredPriority 
                        && prevResult.Priority < priority)
                            prevResult.Priority = priority;
                }
            }
        }

        private string removeDefaultPortFromUrl(string url)
        {
            var uri = new UriBuilder(url);
            string result = url;
            if (uri.Scheme.Equals("http") && uri.Port == 80)
                result = url.Replace(string.Format("{0}:{1}", uri.Host, uri.Port), uri.Host);
            return result;
        }

        private bool isCrawlerTarget(string url)
        {
            var uri =new UriBuilder(url);
            return !(uri.Host != PrimaryHost
                                    || uri.Uri.ToString().ToLower().EndsWith("form") // just because 
                                    || uri.Uri.ToString().ToLower().Contains(PrimaryHost + "/content/") // avoid images and static content
                                    || uri.Uri.ToString().ToLower().Contains("mailto:") // avoid mailto 
                                    || uri.Uri.ToString().ToLower().Contains("javascript:") // avoid javascript:
                                    || uri.Uri.ToString().ToLower().Contains("tel:")); // avoid tel javascript:
        }

        private IEnumerable<string> getReferences(HtmlDocument doc)
        {
            var references = new List<string>();

            var refNodes = doc.DocumentNode.SelectNodes("//a").ToArray();

            foreach (var node in refNodes)
            {
                HtmlAttribute aAttribute = node.Attributes["href"];
                if (aAttribute != null)
                {
                    string urlValue = aAttribute.Value;
                    if (!string.IsNullOrWhiteSpace(urlValue) && !urlValue.Trim().StartsWith("#"))
                    {
                        string tempStr = urlValue;
                        if (tempStr.StartsWith(".."))
                            tempStr = tempStr.Replace("..", "").Trim();
                        if (!urlValue.Contains("http://") && !urlValue.Contains("https://"))
                        {
                            tempStr = (
                                (tempStr.StartsWith("/") && StartingUrl.EndsWith("/"))
                                    ? StartingUrl.Substring(0, StartingUrl.Length - 1)
                                    : StartingUrl)
                                + tempStr;
                        }
                        references.Add(tempStr);
                    }
                }
            }
            return references;
        }

        private ReadPage ReadPage(string url)
        {
            var page = new ReadPage()
            {
                Url = url,
            };

            HttpWebRequest webRequest = HttpWebRequest.Create(url) as HttpWebRequest;
            if (webRequest == null)
            {
                return page;
            }
            HttpWebResponse webResponse = null;
            try 
	        {
                webResponse = webRequest.GetResponse() as HttpWebResponse;
	        }
	        catch (WebException we)
	        {
                if(we.Message.Contains("404"))
                {
                    page.StatusCode = HttpStatusCode.NotFound;
                }
	        };
            if (webResponse == null)
            {
                return page;
            }

            Stream data = webResponse.GetResponseStream();
            string html = String.Empty;
            HtmlDocument doc = new HtmlDocument();
            using (StreamReader sr = new StreamReader(data))
            {
                html = sr.ReadToEnd();
                doc.LoadHtml(html);
            }
            if (webResponse.ContentType.Contains("text/html"))
            {
                page.Document = doc;
            }

            page.StatusCode = webResponse.StatusCode; 

            return page;
        }
    }

    public class CrawledItem
    {
        private ReadPage pageContent;

        public IEnumerable<CrawledItem> Links { get; set; }
        public int Priority { get; set; }
        public bool IsDeclaredPriority { get; set; }
        public string ChangeFrequency { get; set; }
        public string LastModificationDate { get; set; }
        public IEnumerable<ImageResultItem> Images { get; set; }
        public bool IsIncluded { get; set; }

        public CrawledItem(ReadPage pageContent, int priority)
        {
            this.pageContent = pageContent;

            int declaredPriority = -1;
            parsePriority(pageContent, ref declaredPriority);
            if (declaredPriority != -1)
            {
                Priority = declaredPriority;
                IsDeclaredPriority = true;
            }
            else
            {
                Priority = priority;
            }

            string changeFreq;
            parseFrequency(pageContent, out changeFreq);
            this.ChangeFrequency = changeFreq;
            string lastMod;
            parseLastModification(pageContent, out lastMod);
            this.LastModificationDate = lastMod;
            IEnumerable<ImageResultItem> images;
            parseImages(pageContent, out images);
            this.Images = images;
            bool isIncluded;
            parseIsIncluded(pageContent, out isIncluded);
            this.IsIncluded = isIncluded;
        }

        private void parseImages(ReadPage pageContent, out IEnumerable<ImageResultItem> images)
        {
            var result = new Dictionary<string, ImageResultItem>();
            var imgList = pageContent.Document.DocumentNode.SelectNodes("//img");
            if (imgList != null && imgList.Any())
            {
                foreach (var imgNode in imgList)
                {
                    var srcAttr = imgNode.Attributes["src"];
                    if (srcAttr != null && !string.IsNullOrWhiteSpace(srcAttr.Value))
                    {
                        string urlValue = srcAttr.Value.Trim().ToLower();
                        string absoluteSource;
                        string PrimaryUrl = new UriBuilder(pageContent.Url).Host;
                        if (!urlValue.Contains("http://") && !urlValue.Contains("https://"))
                        {
                            absoluteSource = ((urlValue.StartsWith("/") && PrimaryUrl.EndsWith("/")) ? PrimaryUrl.Substring(0, PrimaryUrl.Length - 1) : PrimaryUrl) + urlValue;
                        }
                        else
                        {
                            absoluteSource = urlValue;
                        }
                        var altAttr = imgNode.Attributes["alt"];
                        var titleAttr = imgNode.Attributes["title"];
                        var img = new ImageResultItem()
                        {
                            Loc = absoluteSource,
                            Caption = altAttr != null ? altAttr.Value.Trim() : null,
                            Title = titleAttr != null ? titleAttr.Value.Trim() : null
                        };
                        if(!result.ContainsKey(img.Loc))
                            result.Add(img.Loc, img);
                    }
                } 
            }
            images = result.Select(x => x.Value).ToList();
        }

        private void parseIsIncluded(ReadPage pageContent, out bool isIncluded)
        {
            var nodes = this.pageContent.Document.DocumentNode.SelectNodes("html/head/meta[@property='article:included_in_sitemap']");
            if (nodes != null)
            {
                HtmlNode metaNode = nodes.FirstOrDefault();
                isIncluded = metaNode.GetAttributeValue("content", false);
            }
            else
                isIncluded = true;
        }

        private void parseLastModification(ReadPage pageContent, out string lastMod)
        {
            var nodes = this.pageContent.Document.DocumentNode.SelectNodes("html/head/meta[@property='article:modified_time']");
            if (nodes != null)
            {
                HtmlNode metaNode = nodes.FirstOrDefault();
                lastMod = metaNode.GetAttributeValue("content", DateTime.Now.ToString());
            }
            else
                lastMod = DateTime.Now.ToString();
        }

        private void parseFrequency(ReadPage pageContent, out string changeFreq)
        {
            var nodes = this.pageContent.Document.DocumentNode.SelectNodes("html/head/meta[@property='article:change_frequency']");
            if (nodes != null)
            {
                HtmlNode metaNode = nodes.FirstOrDefault();
                changeFreq = metaNode.GetAttributeValue("content", DateTime.Now.ToShortDateString());
            }
            else
                changeFreq = "weekly";
        }

        private void parsePriority(ReadPage pageContent, ref int priority)
        {
            var nodes = this.pageContent.Document.DocumentNode.SelectNodes("html/head/meta[@property='article:priority']");
            if (nodes != null)
            {
                HtmlNode metaNode = nodes.FirstOrDefault();
                priority = metaNode.GetAttributeValue("content", priority);
            }
        }
    }

    public class ReadPage : IDisposable
    {

        public HtmlDocument Document { get; set; }

        public HttpStatusCode StatusCode { get; set; }

        public string Url { get; set; }

        public void Dispose()
        {
            Document = null;
        }
    }

    public class ParseResultItem
    {
        public int Priority { get; set; }

        public string Url { get; set; }

        public string Changefreq { get; set; }

        public string LastModificationDate { get; set; }

        public IEnumerable<ImageResultItem> Images { get; set; }

        public ParseResultItem(KeyValuePair<string, CrawledItem> x)
        {
            this.Priority = x.Value.Priority;
            this.Url = x.Key;
            this.Changefreq = x.Value.ChangeFrequency;
            this.LastModificationDate = x.Value.LastModificationDate;
            this.Images = x.Value.Images;
        }
    }

    public class ImageResultItem
    {
        public string Loc { get; set; }

        public string Caption { get; set; }

        public string Title { get; set; }
    }
}