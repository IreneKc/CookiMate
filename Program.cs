using CookiMateWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

builder.Services.AddDistributedMemoryCache();

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(60);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddHttpClient("CookiMateApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiSettings:BaseUrl"] ?? "http://127.0.0.1:8000");
    client.DefaultRequestHeaders.Add("X-Api-Key",
        builder.Configuration["ApiSettings:ApiKey"]
        ?? throw new InvalidOperationException("ApiSettings:ApiKey is missing."));
});

builder.Services.AddScoped<CookiMateApiService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.UseSession();

//app.Use(async (context, next) =>
//{
//    if (context.Request.Path == "/")
//    {
//        if (context.Session.GetString("UserID") == null)
//        {
//            context.Response.Redirect("/Login");
//            return;
//        }
//        else
//        {
//            context.Response.Redirect("/Index");
//            return;
//        }
//    }

//    await next();
//});

app.MapRazorPages();

app.Run();