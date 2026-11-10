using Meridian.Web;

// Container HEALTHCHECK: `dotnet Meridian.Web.dll --healthcheck [url]` (no curl needed in the image).
if (args is ["--healthcheck", ..]) return await Meridian.Web.Hosting.HealthProbe.RunAsync(args);

var app = await MeridianServer.CreateAsync(args);
app.Run();
return 0;
