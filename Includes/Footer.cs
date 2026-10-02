using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CookiMateWeb.Includes
{
    public class Footer
    {

        #region Theme 
        public static string GetThemeHtml(ViewDataDictionary ViewData, HttpContext httpContext, string footerHTML = "")
        {
            footerHTML += "<!--begin::Footer-->";
            //footerHTML += "<footer class='fade-in bg-gradient-to-br from-[#f7fbf4] via-[#eef8e8] to-[#f7fbf4] border-t border-green-100 font-[Inter]'>";
            footerHTML += "<footer class='bg-gradient-to-br from-[#f7fbf4] via-[#eef8e8] to-[#f7fbf4] border-t border-green-100 font-[Inter]'>";
            footerHTML += "    <div class='max-w-7xl mx-auto px-6 lg:px-8 py-8'>";
            footerHTML += "        <div class='grid grid-cols-1 md:grid-cols-3 gap-10 items-start'>";
            //BRAND
            footerHTML += "            <div>";
            footerHTML += "                <a href='/' class='inline-block mb-4'>";
            footerHTML += "                    <img src='/images/logo-light.png' alt='CookiMate Logo' class='h-14 w-auto object-contain drop-shadow-sm' />";
            footerHTML += "                </a>";
            footerHTML += "                <p class='text-base text-gray-600 leading-7 max-w-sm font-[Inter]'>";
            footerHTML += "                    Your smart recipe companion for discovering recipes, improving search accuracy, and getting better personalized recommendations.";
            footerHTML += "                </p>";
            footerHTML += "            </div>";
            //QUICK LINKS
            footerHTML += "            <div>";
            footerHTML += "                <h3 class='text-xl font-[Playfair Display] font-semibold text-[#2f5d2f] mb-4 tracking-wide'>Quick Links</h3>";
            footerHTML += "                <ul class='space-y-3'>";
            footerHTML += "                    <li><a href='/' class='text-base text-gray-600 hover:text-green-600 transition hover:translate-x-1 inline-block'>Home</a></li>";
            footerHTML += "                    <li><a href='/Search' class='text-base text-gray-600 hover:text-green-600 transition hover:translate-x-1 inline-block'>Search Recipes</a></li>";
            footerHTML += "                    <li><a href='/Recommend' class='text-base text-gray-600 hover:text-green-600 transition hover:translate-x-1 inline-block'>Recommendations</a></li>";
            footerHTML += "                    <li><a href='/Login' class='text-base text-gray-600 hover:text-green-600 transition hover:translate-x-1 inline-block'>Login</a></li>";
            footerHTML += "                </ul>";
            footerHTML += "            </div>";
            //EXTRA SECTION
            footerHTML += "            <div>";
            footerHTML += "                <h3 class='text-xl font-[Playfair Display] font-semibold text-[#2f5d2f] mb-4 tracking-wide'>About CookiMate</h3>";
            footerHTML += "                <p class='text-base text-gray-600 leading-7'>";
            footerHTML += "                    Built as an AI-driven FYP prototype using TF-IDF search and ranking-based recommendation to enhance recipe discovery and user experience.";
            footerHTML += "                </p>";
            footerHTML += "                <div class='mt-4 h-[2px] w-16 bg-gradient-to-r from-green-500 to-[#7bc043] rounded-full'></div>";
            footerHTML += "            </div>";
            footerHTML += "        </div>";
            //BOTTOM
            footerHTML += "        <div class='mt-8 pt-5 border-t border-green-100 flex flex-col md:flex-row justify-between items-center gap-3'>";
            footerHTML += "            <p class='text-sm text-gray-500'>";
            footerHTML += "                © 2026 <span class='font-semibold text-[#2f5d2f]'>CookiMate</span>. All rights reserved.";
            footerHTML += "            </p>";
            footerHTML += "            <p class='text-sm text-gray-500'>";
            footerHTML += "                Designed for <span class='font-medium text-green-600'>FYP Prototype</span>";
            footerHTML += "            </p>";
            footerHTML += "        </div>";
            footerHTML += "    </div>";
            footerHTML += "</footer>";
            footerHTML += "<!--end::Footer-->";

            return footerHTML;
        }
        #endregion

    }
}
