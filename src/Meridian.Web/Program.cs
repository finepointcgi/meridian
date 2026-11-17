using Meridian.Web;

// Container HEALTHCHECK: `dotnet Meridian.Web.dll --healthcheck [url]` (no curl needed in the image).
if (args is ["--healthcheck", ..]) return await Meridian.Web.Hosting.HealthProbe.RunAsync(args);

Meridian.Web.Hosting.CrashReports.InstallProcessHandlers();   // unhandled exceptions are logged, redacted (Hosting/ProblemReports.cs)
var app = await MeridianServer.CreateAsync(args);
app.Run();
return 0;
