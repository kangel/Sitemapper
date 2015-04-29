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

                var crawler = new WideCrawler()
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

    #region wideCrawler
    public class WideCrawler
    {
        public List<PendingTarget> NextLevelTargets { get; set; }
        
        public string StartingUrl { get; set; }

        public string PrimaryHost { get; set; }

        public Dictionary<string, CrawledItem> Results { get; set; }

        public int MaxPriority { get; set; }

        public int MinPriority { get; set; }
        
        public WideCrawler(int maxPriority = 10, int minPriority = 1)
        {
            this.MaxPriority = maxPriority;
            this.MinPriority = minPriority;
        }

        public IEnumerable<ParseResultItem> parseSite()
        {
            Results = new Dictionary<string,CrawledItem>();

            int level = 0;

            NextLevelTargets = new List<PendingTarget>();

            NextLevelTargets.Add(new PendingTarget()
            {
                Url = StartingUrl,
                Level = level,
            });

            while (NextLevelTargets.Any())
            {
                parseLevel();

                level++;
            }
            var result = Results
                .Where(x => x.Value != null && x.Value.IsIncluded)
                .ToArray()
                .Select(x => new ParseResultItem(x));

            return result;
        }

        private void parseLevel()
        {
            var currentLevel = NextLevelTargets;
            
            NextLevelTargets = new List<PendingTarget>();

            foreach(var target in currentLevel)
            {
                var foundTargets = parsePage(target);
                var newTargets = foundTargets.Where(t => !NextLevelTargets.Select(t1 => t1.Url).Any(s => s.Equals(t.Url)));
                NextLevelTargets.AddRange(newTargets);
            }
        }

        private IEnumerable<PendingTarget> parsePage(PendingTarget target)
        {
            IEnumerable<PendingTarget> references = new PendingTarget[] {};

            target.Url = target.Url.ToConventionalUrl();
            
            if (target.Url.IsCrawlerTargetAt(PrimaryHost))
            {
                if (!Results.ContainsKey(target.Url))
                {

                    CrawledItem crawledItem = null;

                    using (var pageContent = ReadPage(target.Url))
                    {
                        if (pageContent != null && pageContent.Document != null)
                        {
                            references = getReferences(pageContent.Document, target);
                            crawledItem = new CrawledItem(pageContent, target, this);
                        }
                    }

                    Results.Add(target.Url, crawledItem);
                }
            }
            return references;
        }

        private IEnumerable<PendingTarget> getReferences(HtmlDocument doc, PendingTarget target)
        {
            var references = new List<PendingTarget>();

            var refNodes = doc.DocumentNode.SelectNodes("//a").ToArray();

            foreach (var node in refNodes)
            {
                HtmlAttribute aAttribute = node.Attributes["href"];
                if (aAttribute != null)
                {
                    string urlValue = aAttribute.Value.ToAbsoluteUrl(this.StartingUrl).ToConventionalUrl();
                    if (!string.IsNullOrWhiteSpace(urlValue) 
                        && !urlValue.StartsWith("#") 
                        && urlValue != target.Url )
                    {
                        string tempStr = urlValue;
                        references.Add(new PendingTarget()
                            {
                                Url = tempStr,
                                Level = target.Level + 1,
                            });
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
                if (we.Message.Contains("404"))
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

    public class PendingTarget
    {

        public string Url { get; set; }

        public int Level { get; set; }
    }
    #endregion

    #region crawlerTools
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

        public CrawledItem(ReadPage pageContent, PendingTarget target, WideCrawler crawler)
            : this(pageContent, crawler.MaxPriority - target.Level > crawler.MinPriority ? crawler.MaxPriority - target.Level : crawler.MinPriority)
        {
            
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
    #endregion

    internal static class Extensions
    {
        // assumes s is trimmed
        internal static string ToAbsoluteUrl(this string s, string StartingUrl)
        {
            string tempStr = s.Trim();

            if (!s.StartsWith("http://") && !s.StartsWith("https://"))
            {
                tempStr = (
                    (tempStr.StartsWith("/") && StartingUrl.EndsWith("/"))
                        ? StartingUrl.Substring(0, StartingUrl.Length - 1)
                        : StartingUrl)
                    + tempStr;
            }

            return tempStr;
        }

        internal static string ToConventionalUrl(this string s)
        {
            // trim it
            s = s.Trim();
            // remove starting .. 
            if (s.StartsWith(".."))
                s = s.Substring(2);
            // to lower
            string result = s.ToLower();
            // remove default port
            var uri = new UriBuilder(result);
            if (uri.Scheme.Equals("http") && uri.Port == 80)
                result = result.Replace(string.Format("{0}:{1}", uri.Host.ToLower(), uri.Port), uri.Host);
            // remove trailing slash
            while (result.EndsWith("/"))
                result = result.Substring(0, result.Length - 1);
            return result;
        }

        internal static bool IsCrawlerTargetAt(this string url, string host)
        {
            var uri = new UriBuilder(url);
            return !(uri.Host != host
                                    || uri.Uri.ToString().ToLower().EndsWith("form") // just because 
                                    || uri.Uri.ToString().ToLower().Contains(host + "/content/") // avoid images and static content
                                    || uri.Uri.ToString().ToLower().Contains("mailto:") // avoid mailto 
                                    || uri.Uri.ToString().ToLower().Contains("javascript:") // avoid javascript:
                                    || uri.Uri.ToString().ToLower().Contains("tel:")); // avoid tel javascript:
        }
    }
}