# Local AI Studio — Release 1.0.1

Browser-based local LLM studio: Angular 20 + ASP.NET Core 8 + Ollama + SQLite.

## Prerequisites
Windows 11, .NET 8 SDK, Node.js 20.19+ or 22.12+, Ollama.
Install Ollama: https://ollama.com/download

## Models
```powershell
ollama pull qwen3:8b
ollama pull qwen2.5-coder:7b
```

## Run backend (PowerShell 1)
```powershell
cd backend\LocalAiStudio.Api
dotnet restore
dotnet run
```
API binds to http://127.0.0.1:5080.

## Run frontend (PowerShell 2)
```powershell
cd frontend
npm install
npm start
```
Open http://localhost:4200.

## Workspaces
Open **Workspaces** in the application to create or select a workspace, browse its
files, preview text files, and create, rename, or delete files and folders.
When creating a workspace, enter its name and optionally an absolute path to an
existing parent folder anywhere the application account can access. The app creates
a new folder with that name there. Leave the location blank to use the configured
workspace root, or use **Browse...** to choose a parent folder from the local folder
browser. The browser lists directories on the machine hosting the backend, exposes
folder names only, and is restricted to loopback requests. Deletion requires
confirmation; file previews are read-only in this phase. File operations remain
confined to the selected workspace, and traversal and symbolic links are rejected.

The default workspace root is `C:\ProgramData\LocalAIStudio\Workspaces`. Set
`LocalAiStudio:WorkspaceRoot` in `backend\LocalAiStudio.Api\appsettings.json` (or
the `LocalAiStudio__WorkspaceRoot` environment variable) to choose another default
folder. Workspace registrations are stored in
`C:\ProgramData\LocalAIStudio\workspaces.json`; configure
`LocalAiStudio:WorkspaceRegistryPath` if needed. Existing projects can be selected
when their directories are under the configured workspace root.
The explorer omits common build/dependency output and sensitive files such as
`.env` files, private keys, and credentials. It also honors root `.gitignore`
rules and patterns in `LocalAiStudio:IgnorePatterns`.

For IIS, create the configured workspace root outside the deployed application
directory and grant the application pool identity **Modify** permission on both
the workspace root and the registry's parent directory. Also grant **Modify**
permission on each parent folder where users are allowed to create workspaces.
For example, from an elevated PowerShell prompt, replace `LocalAiStudioPool` with
the IIS application pool name:
```powershell
New-Item -ItemType Directory -Force C:\ProgramData\LocalAIStudio\Workspaces
icacls C:\ProgramData\LocalAIStudio\Workspaces /grant "IIS AppPool\LocalAiStudioPool:(OI)(CI)M"
```

## Release 1
- Ollama health, installed models and running models
- Load/switch/unload model (the API unloads other models before loading the selected model)
- Streaming chat, chat history in SQLite
- Chat, analysis and code-generation modes
- Code block extraction, copy and download
- Workspace selection, file browsing, previews, and confirmed file operations

## Hardware
For Ryzen 7 5700U / 32 GB RAM: use one loaded model at a time, 4096 context. CPU inference speed depends on thermals, power mode, quantization and context.

## Notes
This is a local development application, not internet-facing. Generated code is text only and is never executed. The app binds the API to loopback. Release 1 does not include MCP or autonomous filesystem/terminal access.
