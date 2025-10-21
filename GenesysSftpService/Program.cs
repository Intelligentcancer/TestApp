using Microsoft.EntityFrameworkCore;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

// NLog
builder.Logging.ClearProviders();
builder.Host.UseNLog();

// Add services to the container.
builder.Services.AddControllersWithViews();

// Configuration options
builder.Services.Configure<GenesysRecordingPostingUtility.Services.ProcessingOptions>(builder.Configuration.GetSection("Processing"));
builder.Services.Configure<GenesysRecordingPostingUtility.Services.SftpOptions>(builder.Configuration.GetSection("Sftp"));
builder.Services.Configure<GenesysSftpService.Controllers.BasicAuthOptions>(builder.Configuration.GetSection("Auth"));

// EF Core
builder.Services.AddDbContext<GenesysRecordingPostingUtility.Models.AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Services
builder.Services.AddScoped<GenesysRecordingPostingUtility.Services.IRecordingDownloader, GenesysRecordingPostingUtility.Services.GenesysRecordingDownloader>();
builder.Services.AddScoped<GenesysRecordingPostingUtility.Services.ISftpUploader, GenesysRecordingPostingUtility.Services.WinScpSftpUploader>();
builder.Services.AddHostedService<GenesysRecordingPostingUtility.Services.RecordingWorker>();

// Auth (cookie-based after basic login)
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
