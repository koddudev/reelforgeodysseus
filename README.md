# ReelForge Odysseus

ASP.NET Core compatibility service for ReelForge/SociVibe media generation.

This is not the full Python Odysseus product. It exposes the Odysseus API surface
that the ReelForge API expects:

- `GET /health`
- `GET /api/default-chat`
- `POST /session`
- `POST /api/chat`

## Run locally

```powershell
dotnet run
```

## Publish

```powershell
dotnet publish -c Release -o publish
Compress-Archive -Path publish\* -DestinationPath ReelForgeOdysseusDotNet.zip -Force
```

Deploy the zip to the `reelforgeodysseus` Azure Web App with a .NET 8 runtime.

Then configure the ReelForge API app setting:

```text
ODYSSEUS_BASE_URL=https://reelforgeodysseus-cpcea6hqddd7hcdj.westus2-01.azurewebsites.net
```
