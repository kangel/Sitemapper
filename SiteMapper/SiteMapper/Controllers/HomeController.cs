using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;

namespace SiteMapper.Controllers
{
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            ViewBag.LastMod = DateTime.Now.ToShortDateString();
            return View();
        }

        public ActionResult About()
        {
            ViewBag.LastMod = "asdf";
            ViewBag.Message = "Your application description page.";
            ViewBag.Included = true.ToString();
            ViewBag.Priority = 5;
            return View();
        }

        public ActionResult Contact()
        {
            //ViewBag.LastMod = DateTime.Now.ToShortDateString();
            ViewBag.Message = "Your contact page.";
            ViewBag.Priority = 4;
            //ViewBag.Included = false.ToString();
            return View();
        }
    }
}