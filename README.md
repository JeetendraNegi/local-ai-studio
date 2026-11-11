# Local AI Studio — Release 1

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

## Release 1
- Ollama health, installed models and running models
- Load/switch/unload model (the API unloads other models before loading the selected model)
- Streaming chat, chat history in SQLite
- Chat, analysis and code-generation modes
- Code block extraction, copy and download

## Hardware
For Ryzen 7 5700U / 32 GB RAM: use one loaded model at a time, 4096 context. CPU inference speed depends on thermals, power mode, quantization and context.

## Notes
This is a local development application, not internet-facing. Generated code is text only and is never executed. The app binds the API to loopback. Release 1 does not include MCP or autonomous filesystem/terminal access.
