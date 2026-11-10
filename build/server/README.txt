Meridian Server
===============

The web version of Meridian: browsers (and the desktop apps' "Team server" mode)
connect to it. Accounts, sharing, live collaboration and the SysML v2 API.

Requires the ASP.NET Core 10 runtime. Run:

  PostgreSQL (recommended for teams):
    ConnectionStrings__Meridian="Host=...;Database=meridian;Username=...;Password=..." \
    dotnet Meridian.Web.dll --urls http://0.0.0.0:8080

  Single file (SQLite, small teams / trials):
    Meridian__Storage=sqlite Meridian__DataPath=/var/lib/meridian \
    dotnet Meridian.Web.dll --urls http://0.0.0.0:8080

Put it behind HTTPS (a reverse proxy such as Caddy or nginx) for anything beyond
a trusted network. Set Meridian__KeysPath to a persistent folder so sign-ins
survive restarts. A Dockerfile and docker-compose.yml are in the source repository.

FMI co-simulation runs FMUs' native code on the server (in a separate
Meridian.FmuHost process per FMU: crash isolation, not a sandbox). It is off on
servers unless Meridian__Fmi__Enabled=true, and only project owners can run it.
See docs/FMI.md in the source repository.
