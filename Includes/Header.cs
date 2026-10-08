using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace CookiMateWeb.Includes
{
    public class Header
    {
        #region Theme
        public async static Task<string> GetThemeHtml(ViewDataDictionary ViewData, HttpContext httpContext, string headerHTML = "")
        {
            string? userId = httpContext.Session.GetString("UserID");
            bool isLoggedIn = !string.IsNullOrEmpty(userId);
            string userRole = httpContext.Session.GetString("UserRole") ?? "user";
            bool isAdmin = userRole.Equals("admin", StringComparison.OrdinalIgnoreCase);
            string userName = httpContext.Session.GetString("UserName") ?? "Guest";
            string userEmail = httpContext.Session.GetString("UserEmail") ?? "guest@email.com";
            string firstLetter = !string.IsNullOrWhiteSpace(userName)
                ? userName.Substring(0, 1).ToUpper()
                : "G";

            
            string savedDiet = isLoggedIn && int.TryParse(userId, out int uid)
                ? await LoadSavedDietAsync(httpContext, uid)
                : "";

            headerHTML += "<!--begin::Header-->";
            headerHTML += "<header class='w-full bg-white shadow-sm border-b border-gray-200 relative z-50'>";
            headerHTML += "    <div class='max-w-7xl mx-auto px-6 lg:px-8'>";
            headerHTML += "        <div class='flex items-center justify-between h-24'>";

            // Left side: Logo + Navigation
            headerHTML += "            <div class='flex items-center space-x-10'>";
            headerHTML += "                <a href='/' class='flex items-center space-x-4 text-decoration-none'>";
            headerHTML += "                    <img src='/images/logo-light.png' alt='CookiMate Logo' class='h-20 w-auto object-contain' />";
            headerHTML += "                </a>";

            headerHTML += "                <nav class='hidden md:flex items-center space-x-6'>";

            // Search box + dropdown
            headerHTML += "                    <div class='relative' id='headerSearchWrapper'>";
            headerHTML += "                    <button id='headerSearchButton' type='button' class='text-lg text-gray-700 font-semibold hover:text-green-600 transition duration-200'>🔍Search Recipes</button>";
            //headerHTML += "                        <button id='headerSearchButton' type='button' class='w-[260px] flex items-center gap-3 px-4 py-3 rounded-xl border border-gray-300 bg-white text-left shadow-sm hover:border-green-400 transition'>";
            //headerHTML += "                            <span class='text-gray-400'>🔍</span>";
            //headerHTML += "                            <span class='text-gray-500 text-sm'>Search recipes...</span>";
            //headerHTML += "                        </button>";

            headerHTML += "                        <div id='headerSearchDropdown' class='hidden absolute left-0 top-full mt-3 w-[390px] bg-white rounded-2xl shadow-2xl border border-gray-200 p-5 z-[9999]'>";
            headerHTML += "                            <form method='get' action='/Search' class='space-y-4'>";
            headerHTML += "                                <div>";
            headerHTML += "                                    <label class='block text-sm font-semibold text-gray-700 mb-2'>Search Keyword</label>";
            headerHTML += "                                    <input type='text' name='q' placeholder='e.g. chicken rice' class='w-full border border-gray-300 rounded-xl px-4 py-3 text-sm focus:outline-none focus:ring-4 focus:ring-green-100 focus:border-green-500' />";
            headerHTML += "                                </div>";

            headerHTML += "                                <div>";
            headerHTML += "                                    <label class='block text-sm font-semibold text-gray-700 mb-2'>Ingredients</label>";
            headerHTML += "                                    <input type='text' name='ingredients' placeholder='e.g. rice, chicken, garlic' class='w-full border border-gray-300 rounded-xl px-4 py-3 text-sm focus:outline-none focus:ring-4 focus:ring-green-100 focus:border-green-500' />";
            headerHTML += "                                    <p class='text-xs text-gray-500 mt-1'>Separate multiple ingredients with commas.</p>";
            headerHTML += "                                </div>";

            headerHTML += "                                <div class='grid grid-cols-2 gap-3'>";
            headerHTML += "                                    <div>";
            headerHTML += "                                        <label class='block text-sm font-semibold text-gray-700 mb-2'>Diet Type</label>";
            headerHTML += "                                        <select name='dietType' class='w-full border border-gray-300 rounded-xl px-4 py-3 text-sm focus:outline-none focus:ring-4 focus:ring-green-100 focus:border-green-500'>";
            headerHTML += $"                                            <option value=''{(savedDiet == "" ? " selected" : "")}>All</option>";
            headerHTML += $"                                            <option value='vegetarian'{(savedDiet == "vegetarian" ? " selected" : "")}>vegetarian</option>";
            headerHTML += $"                                            <option value='vegan'{(savedDiet == "vegan" ? " selected" : "")}>vegan</option>";
            headerHTML += $"                                            <option value='halal'{(savedDiet == "halal" ? " selected" : "")}>halal</option>";
            headerHTML += $"                                            <option value='low-carb'{(savedDiet == "low-carb" ? " selected" : "")}>low-carb</option>";
            headerHTML += "                                        </select>";
            headerHTML += "                                    </div>";

            headerHTML += "                                    <div>";
            headerHTML += "                                        <label class='block text-sm font-semibold text-gray-700 mb-2'>Cuisine</label>";
            headerHTML += "                                        <select name='cuisine' class='w-full border border-gray-300 rounded-xl px-4 py-3 text-sm focus:outline-none focus:ring-4 focus:ring-green-100 focus:border-green-500'>";
            headerHTML += "                                            <option value=''>All</option>";
            headerHTML += "                                            <option value='Chinese'>Chinese</option>";
            headerHTML += "                                            <option value='Malay'>Malay</option>";
            headerHTML += "                                            <option value='Western'>Western</option>";
            headerHTML += "                                            <option value='Indian'>Indian</option>";
            headerHTML += "                                        </select>";
            headerHTML += "                                    </div>";
            headerHTML += "                                </div>";

            headerHTML += "                                <div class='flex items-center gap-3 pt-1'>";
            headerHTML += "                                    <button type='submit' class='px-5 py-3 text-white rounded-xl font-medium shadow-md transition hover:opacity-90' style='background: linear-gradient(135deg, #4e9f3d, #7bc043);'>Search</button>";
            //headerHTML += "                                    <a href='/Search' class='px-4 py-3 rounded-xl font-medium border border-gray-300 text-gray-600 hover:bg-gray-50 transition'>Advanced Search</a>";
            headerHTML += "                                </div>";
            headerHTML += "                            </form>";
            headerHTML += "                        </div>";
            headerHTML += "                    </div>";

            headerHTML += "                    <a href='/Recommend' class='text-lg text-gray-700 font-semibold hover:text-green-600 transition duration-200'>🥦Recommendations</a>";
            headerHTML += "                    <a href='/MealPlan' class='text-lg text-gray-700 font-semibold hover:text-green-600 transition duration-200'>📅Meal Plan</a>";
            headerHTML += "                    <a href='/MyRecipes' class='flex items-center gap-2 px-5 py-2.5 text-white rounded-xl font-medium shadow-md transition hover:opacity-90' style='background: linear-gradient(135deg, #4e9f3d, #7bc043);'>";
            headerHTML += "                        <i class='fa-solid fa-carrot'></i> My Recipes";
            headerHTML += "                    </a>";
            if (isAdmin)
            {
                headerHTML += "                    <a href='/ManageRecipe' class='flex items-center gap-2 px-5 py-2.5 text-white rounded-xl font-medium shadow-md transition hover:opacity-90' style='background: linear-gradient(135deg, #b91c1c, #ef4444);'>";
                headerHTML += "                        <i class='fa-solid fa-shield-halved'></i> Manage Recipes";
                headerHTML += "                    </a>";
            }
            headerHTML += "                </nav>";
            headerHTML += "            </div>";

            // Right side: User dropdown
            headerHTML += "            <div class='flex items-center gap-4 relative z-50'>";

            if (!isLoggedIn)
            {
                headerHTML += "<a href='/Login' class='px-5 py-2.5 rounded-xl font-medium transition duration-300 border-1 text-green-600 hover:text-white' style='border-image: linear-gradient(135deg, #4e9f3d, #7bc043) 1; background-color: white;' onmouseover=\"this.style.background='linear-gradient(135deg, #4e9f3d, #7bc043)';\" onmouseout=\"this.style.background='white';\">Login</a>";
            }

            if (isLoggedIn)
            {
                headerHTML += "<button id='userMenuButton' type='button' class='flex items-center justify-center w-12 h-12 rounded-full bg-green-100 hover:bg-green-200 transition border border-green-300 shadow-sm'>";
            }
            else
            {
                headerHTML += "<div class='flex items-center justify-center w-12 h-12 rounded-full bg-gray-200 text-gray-400 border border-gray-300 cursor-not-allowed'>";
            }

            headerHTML += "<svg xmlns='http://www.w3.org/2000/svg' class='w-7 h-7 text-green-700' fill='none' viewBox='0 0 24 24' stroke='currentColor'>";
            headerHTML += "<path stroke-linecap='round' stroke-linejoin='round' stroke-width='2' d='M15.75 6.75a3.75 3.75 0 11-7.5 0 3.75 3.75 0 017.5 0zM4.501 20.118a7.5 7.5 0 0114.998 0A17.933 17.933 0 0112 21.75c-2.676 0-5.216-.584-7.499-1.632z' />";
            headerHTML += "</svg>";
            headerHTML += isLoggedIn ? "</button>" : "</div>";

            if (isLoggedIn)
            {
                headerHTML += "<div class='relative'>";
                headerHTML += "<div id='userDropdownMenu' class='hidden absolute right-0 mt-3 w-64 bg-white rounded-2xl shadow-xl border border-gray-200 overflow-hidden z-[9999]'>";
                headerHTML += "<div class='px-4 py-4 border-b border-gray-100'>";
                headerHTML += "<div class='flex items-center gap-3'>";
                headerHTML += $"<div class='w-11 h-11 rounded-full bg-green-500 text-white flex items-center justify-center font-bold text-lg'>{firstLetter}</div>";
                headerHTML += "<div>";
                headerHTML += $"<p class='text-sm font-semibold text-gray-800'>{userName}</p>";
                headerHTML += $"<p class='text-xs text-gray-500'>{userEmail}</p>";
                headerHTML += "</div>";
                headerHTML += "</div>";
                headerHTML += "</div>";

                headerHTML += "<div class='py-2'>";
                headerHTML += "<a href='/Profile' class='block px-4 py-3 text-sm text-gray-700 hover:bg-green-50'>My Profile</a>";
                headerHTML += "<a href='/Account' class='block px-4 py-3 text-sm text-gray-700 hover:bg-green-50'>My Account</a>";
                headerHTML += "<a href='/Favorites' class='block px-4 py-3 text-sm text-gray-700 hover:bg-green-50'>Favorites</a>";
                headerHTML += "<a href='/Settings' class='block px-4 py-3 text-sm text-gray-700 hover:bg-green-50'>Settings</a>";
                headerHTML += "</div>";

                headerHTML += "<div class='border-t border-gray-100 px-4 py-3'>";
                headerHTML += "<a href='/Logout' class='block w-full text-center text-white font-medium py-2.5 rounded-xl transition shadow-md hover:opacity-90' style='background: linear-gradient(135deg, #4e9f3d, #7bc043);'>Log out</a>";
                headerHTML += "</div>";
                headerHTML += "</div>";
                headerHTML += "</div>";
            }

            headerHTML += "            </div>";
            headerHTML += "        </div>";
            headerHTML += "    </div>";
            headerHTML += "</header>";
            headerHTML += "<!--end::Header-->";

            // Script
            headerHTML += "<script>";
            headerHTML += "document.addEventListener('DOMContentLoaded', function () {";

            if (isLoggedIn)
            {
                headerHTML += "const button = document.getElementById('userMenuButton');";
                headerHTML += "const menu = document.getElementById('userDropdownMenu');";
                headerHTML += "if(button && menu){";
                headerHTML += "button.addEventListener('click', function(e){ e.stopPropagation(); menu.classList.toggle('hidden'); });";
                headerHTML += "document.addEventListener('click', function(e){ if(!menu.contains(e.target) && !button.contains(e.target)){ menu.classList.add('hidden'); }});";
                headerHTML += "}";
            }

            headerHTML += "const searchButton = document.getElementById('headerSearchButton');";
            headerHTML += "const searchDropdown = document.getElementById('headerSearchDropdown');";
            headerHTML += "const searchWrapper = document.getElementById('headerSearchWrapper');";
            headerHTML += "if(searchButton && searchDropdown && searchWrapper){";
            headerHTML += "searchButton.addEventListener('click', function(e){ e.stopPropagation(); searchDropdown.classList.toggle('hidden'); });";
            headerHTML += "document.addEventListener('click', function(e){ if(!searchWrapper.contains(e.target)){ searchDropdown.classList.add('hidden'); }});";
            headerHTML += "}";

            headerHTML += "});";
            headerHTML += "</script>";

            return headerHTML;
        }
        #endregion

        #region Saved diet lookup
        private static async Task<string> LoadSavedDietAsync(HttpContext httpContext, int userId)
        {
            try
            {
                var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
                string connectionString =
                    configuration.GetConnectionString("Default")
                    ?? configuration.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Database connection string is missing.");

                await using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                const string dietSql = "SELECT diet FROM users WHERE user_id = @UserID LIMIT 1;";
                await using var cmd = new MySqlCommand(dietSql, connection);
                cmd.Parameters.AddWithValue("@UserID", userId);
                var result = await cmd.ExecuteScalarAsync();
                return (result as string)?.Trim().ToLowerInvariant() ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }
        #endregion
    }
}