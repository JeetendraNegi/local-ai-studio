//using Microsoft.EntityFrameworkCore;
//using System.Text.Json;
//var builder = WebApplication.CreateBuilder(args);
//builder.WebHost.UseUrls("http://127.0.0.1:5080");
//builder.Services.AddDbContext<StudioDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Studio")));
//builder.Services.AddHttpClient("ollama", c => { c.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434"); c.Timeout = Timeout.InfiniteTimeSpan; });
//builder.Services.AddCors(o => o.AddPolicy("dev", p => p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));
//builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen();
//var app = builder.Build();
//using (var s = app.Services.CreateScope()) await s.ServiceProvider.GetRequiredService<StudioDb>().Database.EnsureCreatedAsync();
//if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
//app.UseCors("dev");
//var gate = new SemaphoreSlim(1, 1);
//HttpClient Ollama(HttpContext c) => c.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("ollama");
//app.MapGet("/api/health", async (HttpContext c) => { try { using var x = await Ollama(c).GetAsync("/api/tags"); return Results.Ok(new { ollama = x.IsSuccessStatusCode ? "connected" : "error" }); } catch { return Results.Ok(new { ollama = "disconnected" }); } });
//app.MapGet("/api/models", async (HttpContext c) => { try { return Results.Json(await Ollama(c).GetFromJsonAsync<JsonElement>("/api/tags")); } catch { return Results.Problem("Ollama unavailable", statusCode: 503); } });
//app.MapGet("/api/models/running", async (HttpContext c) => { try { return Results.Json(await Ollama(c).GetFromJsonAsync<JsonElement>("/api/ps")); } catch { return Results.Problem("Ollama unavailable", statusCode: 503); } });
//app.MapPost("/api/models/load", async (ModelReq req, HttpContext c) =>
//{
//    await gate.WaitAsync(c.RequestAborted); try
//    {
//        var client = Ollama(c); var ps = await client.GetFromJsonAsync<JsonElement>("/api/ps", c.RequestAborted);
//        if (ps.TryGetProperty("models", out var arr)) foreach (var m in arr.EnumerateArray()) { var n = m.GetProperty("name").GetString(); if (n != null && !n.Equals(req.Model, StringComparison.OrdinalIgnoreCase)) await client.PostAsJsonAsync("/api/generate", new { model = n, keep_alive = 0 }, c.RequestAborted); }
//        var response = await client.PostAsJsonAsync("/api/generate", new { model = req.Model, prompt = "", stream = false, keep_alive = "10m", options = new { num_ctx = 4096 } }, c.RequestAborted);
//        if (!response.IsSuccessStatusCode) return Results.Problem(await response.Content.ReadAsStringAsync(), statusCode: 502);
//        return Results.Ok(new { loaded = req.Model });
//    }
//    catch (Exception e) { return Results.Problem(e.Message, statusCode: 502); }
//    finally { gate.Release(); }
//});
//app.MapPost("/api/models/unload", async (ModelReq req, HttpContext c) => { try { var x = await Ollama(c).PostAsJsonAsync("/api/generate", new { model = req.Model, keep_alive = 0 }, c.RequestAborted); return x.IsSuccessStatusCode ? Results.Ok(new { unloaded = req.Model }) : Results.Problem(await x.Content.ReadAsStringAsync(), statusCode: 502); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 502); } });
//app.MapPost("/api/models/pull", async (ModelReq req, HttpContext c) =>
//{
//    c.Response.ContentType = "text/event-stream"; try { using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pull") { Content = JsonContent.Create(new { model = req.Model, stream = true }) }; using var resp = await Ollama(c).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, c.RequestAborted); resp.EnsureSuccessStatusCode(); using var stream = await resp.Content.ReadAsStreamAsync(c.RequestAborted); using var reader = new StreamReader(stream); while (!reader.EndOfStream) { var line = await reader.ReadLineAsync(c.RequestAborted); if (!string.IsNullOrWhiteSpace(line)) { await c.Response.WriteAsync("data: " + line + "\n\n", c.RequestAborted); await c.Response.Body.FlushAsync(c.RequestAborted); } } await c.Response.WriteAsync("event: done\ndata: {}\n\n"); } catch (Exception e) { await c.Response.WriteAsync("event: error\ndata: " + JsonSerializer.Serialize(new { error = e.Message }) + "\n\n"); }
//});
//app.MapGet("/api/conversations", async (StudioDb db) => Results.Ok(await db.Conversations.OrderByDescending(x => x.UpdatedAt).Select(x => new { x.Id, x.Title, x.Model, x.Mode, x.UpdatedAt }).ToListAsync()));
//app.MapPost("/api/conversations", async (ConversationReq req, StudioDb db) => { var x = new Conversation { Title = string.IsNullOrWhiteSpace(req.Title) ? "New conversation" : req.Title, Model = req.Model, Mode = req.Mode ?? "chat" }; db.Conversations.Add(x); await db.SaveChangesAsync(); return Results.Ok(x); });
//app.MapGet("/api/conversations/{id:int}/messages", async (int id, StudioDb db) => Results.Ok(await db.Messages.Where(x => x.ConversationId == id).OrderBy(x => x.Id).ToListAsync()));
//app.MapPost("/api/chat/stream", async (ChatReq req, HttpContext c, StudioDb db) =>
//{
//    c.Response.ContentType = "text/event-stream"; if (string.IsNullOrWhiteSpace(req.Model) || string.IsNullOrWhiteSpace(req.Message)) { c.Response.StatusCode = 400; return; }
//    var conv = req.ConversationId.HasValue ? await db.Conversations.FindAsync(new object[] { req.ConversationId.Value }, c.RequestAborted) : null;
//    if (conv == null) { conv = new Conversation { Title = req.Message[..Math.Min(48, req.Message.Length)], Model = req.Model, Mode = req.Mode ?? "chat" }; db.Conversations.Add(conv); await db.SaveChangesAsync(c.RequestAborted); }
//    db.Messages.Add(new ChatMessage { ConversationId = conv.Id, Role = "user", Content = req.Message }); conv.Model = req.Model; conv.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(c.RequestAborted);
//    var hist = await db.Messages.Where(x => x.ConversationId == conv.Id).OrderBy(x => x.Id).Take(20).ToListAsync(c.RequestAborted);
//    var system = req.Mode == "code" ? "You are a senior software developer. Provide correct code with file names and explanations. Do not claim code was run." : req.Mode == "analysis" ? "You are a careful analyst. Structure assumptions, analysis, findings and limitations." : "You are a helpful assistant. Be accurate and concise.";
//    var msgs = new List<object> { new { role = "system", content = system } }; msgs.AddRange(hist.Select(x => (object)new { role = x.Role, content = x.Content }));
//    var payload = new { model = req.Model, stream = true, messages = msgs, options = new { num_ctx = 4096 } };
//    var answer = ""; try
//    {
//        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(payload) }; using var resp = await Ollama(c).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, c.RequestAborted); resp.EnsureSuccessStatusCode(); using var stream = await resp.Content.ReadAsStreamAsync(c.RequestAborted); using var reader = new StreamReader(stream); while (!reader.EndOfStream) { var line = await reader.ReadLineAsync(c.RequestAborted); if (string.IsNullOrWhiteSpace(line)) continue; using var doc = JsonDocument.Parse(line); var root = doc.RootElement; if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content)) { var token = content.GetString() ?? ""; answer += token; await c.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { token, conversationId = conv.Id }) + "\n\n", c.RequestAborted); await c.Response.Body.FlushAsync(c.RequestAborted); } if (root.TryGetProperty("done", out var done) && done.GetBoolean()) break; }
//        db.Messages.Add(new ChatMessage { ConversationId = conv.Id, Role = "assistant", Content = answer }); conv.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(c.RequestAborted); await c.Response.WriteAsync("event: done\ndata: {}\n\n", c.RequestAborted);
//    }
//    catch (OperationCanceledException) { }
//    catch (Exception e) { await c.Response.WriteAsync("event: error\ndata: " + JsonSerializer.Serialize(new { error = e.Message }) + "\n\n"); }
//});
//app.Run();
//record ModelReq(string Model);
//record ConversationReq(string? Title, string Model, string? Mode);
//record ChatReq(string Model, string Message, int? ConversationId, string? Mode);
//class Conversation { public int Id { get; set; } public string Title { get; set; } = "New conversation"; public string Model { get; set; } = ""; public string Mode { get; set; } = "chat"; public DateTime UpdatedAt { get; set; } = DateTime.UtcNow; public List<ChatMessage> Messages { get; set; } = []; }
//class ChatMessage { public int Id { get; set; } public int ConversationId { get; set; } public string Role { get; set; } = "user"; public string Content { get; set; } = ""; }
//class StudioDb(DbContextOptions<StudioDb> options) : DbContext(options) { public DbSet<Conversation> Conversations => Set<Conversation>(); public DbSet<ChatMessage> Messages => Set<ChatMessage>(); protected override void OnModelCreating(ModelBuilder b) => b.Entity<Conversation>().HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade); }





using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Database configuration
builder.Services.AddDbContext<StudioDb>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("Studio")));
builder.Services.AddSingleton<WorkspaceService>();

// Ollama HTTP client
builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434");

    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Initialize SQLite database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
    await db.Database.EnsureCreatedAsync();
}

// Swagger only in development
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Serve Angular application from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

var gate = new SemaphoreSlim(1, 1);

HttpClient Ollama(HttpContext context) =>
    context.RequestServices
        .GetRequiredService<IHttpClientFactory>()
        .CreateClient("ollama");

var workspaceApi = app.MapGroup("/api/workspaces");
workspaceApi.AddEndpointFilter(async (context, next) =>
{
    try
    {
        return await next(context);
    }
    catch (WorkspaceException exception)
    {
        return Results.Problem(
            statusCode: exception.StatusCode,
            title: "Workspace request failed",
            detail: exception.Message);
    }
});

workspaceApi.MapGet("", (WorkspaceService workspaces) =>
    Results.Ok(workspaces.ListWorkspaces()));

workspaceApi.MapPost("", (CreateWorkspaceRequest request, WorkspaceService workspaces) =>
    Results.Ok(workspaces.CreateWorkspace(request.Name, request.Location)));

workspaceApi.MapGet("/browse", (
    string? path,
    HttpContext context,
    WorkspaceService workspaces) =>
{
    if (context.Connection.RemoteIpAddress is not { } address ||
        !IPAddress.IsLoopback(address))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Folder browsing is available only from this machine.");
    }

    return Results.Ok(workspaces.BrowseDirectories(path));
});

workspaceApi.MapGet("/{workspace}/files", (
    string workspace,
    string? path,
    WorkspaceService workspaces) =>
    Results.Ok(workspaces.ListEntries(workspace, path)));

workspaceApi.MapGet("/{workspace}/files/content", async (
    string workspace,
    string? path,
    WorkspaceService workspaces,
    HttpContext context) =>
    Results.Ok(await workspaces.ReadFileAsync(
        workspace,
        path,
        context.RequestAborted)));

workspaceApi.MapPost("/{workspace}/files", async (
    string workspace,
    CreateWorkspaceFileRequest request,
    WorkspaceService workspaces,
    HttpContext context) =>
    Results.Ok(await workspaces.CreateFileAsync(
        workspace,
        request.Path,
        request.Content,
        context.RequestAborted)));

workspaceApi.MapPost("/{workspace}/directories", (
    string workspace,
    CreateWorkspaceDirectoryRequest request,
    WorkspaceService workspaces) =>
    Results.Ok(workspaces.CreateDirectory(workspace, request.Path)));

workspaceApi.MapPost("/{workspace}/files/rename", (
    string workspace,
    RenameWorkspaceEntryRequest request,
    WorkspaceService workspaces) =>
    Results.Ok(workspaces.Rename(
        workspace,
        request.Path,
        request.NewName,
        isDirectory: false)));

workspaceApi.MapPost("/{workspace}/directories/rename", (
    string workspace,
    RenameWorkspaceEntryRequest request,
    WorkspaceService workspaces) =>
    Results.Ok(workspaces.Rename(
        workspace,
        request.Path,
        request.NewName,
        isDirectory: true)));

workspaceApi.MapDelete("/{workspace}/files", (
    string workspace,
    [FromBody] DeleteWorkspaceEntryRequest request,
    WorkspaceService workspaces) =>
{
    workspaces.Delete(
        workspace,
        request.Path,
        isDirectory: false,
        request.Confirmed);
    return Results.NoContent();
});

workspaceApi.MapDelete("/{workspace}/directories", (
    string workspace,
    [FromBody] DeleteWorkspaceEntryRequest request,
    WorkspaceService workspaces) =>
{
    workspaces.Delete(
        workspace,
        request.Path,
        isDirectory: true,
        request.Confirmed);
    return Results.NoContent();
});

// Health check
app.MapGet("/api/health", async (HttpContext context) =>
{
    try
    {
        using var response = await Ollama(context).GetAsync("/api/tags");

        return Results.Ok(new
        {
            ollama = response.IsSuccessStatusCode
                ? "connected"
                : "error"
        });
    }
    catch
    {
        return Results.Ok(new { ollama = "disconnected" });
    }
});

// Get installed models
app.MapGet("/api/models", async (HttpContext context) =>
{
    try
    {
        return Results.Json(
            await Ollama(context).GetFromJsonAsync<JsonElement>("/api/tags"));
    }
    catch
    {
        return Results.Problem("Ollama unavailable", statusCode: 503);
    }
});

// Get currently running models
app.MapGet("/api/models/running", async (HttpContext context) =>
{
    try
    {
        return Results.Json(
            await Ollama(context).GetFromJsonAsync<JsonElement>("/api/ps"));
    }
    catch
    {
        return Results.Problem("Ollama unavailable", statusCode: 503);
    }
});

// Load model (unloads other models first)
app.MapPost("/api/models/load", async (ModelReq req, HttpContext context) =>
{
    await gate.WaitAsync(context.RequestAborted);

    try
    {
        var client = Ollama(context);

        var running = await client.GetFromJsonAsync<JsonElement>(
            "/api/ps", context.RequestAborted);

        if (running.TryGetProperty("models", out var models))
        {
            foreach (var model in models.EnumerateArray())
            {
                var name = model.GetProperty("name").GetString();

                if (name != null &&
                    !name.Equals(req.Model, StringComparison.OrdinalIgnoreCase))
                {
                    await client.PostAsJsonAsync(
                        "/api/generate",
                        new { model = name, keep_alive = 0 },
                        context.RequestAborted);
                }
            }
        }

        var response = await client.PostAsJsonAsync(
            "/api/generate",
            new
            {
                model = req.Model,
                prompt = "",
                stream = false,
                keep_alive = "10m",
                options = new { num_ctx = 4096 }
            },
            context.RequestAborted);

        if (!response.IsSuccessStatusCode)
        {
            return Results.Problem(
                await response.Content.ReadAsStringAsync(),
                statusCode: 502);
        }

        return Results.Ok(new { loaded = req.Model });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
    finally
    {
        gate.Release();
    }
});

// Unload model
app.MapPost("/api/models/unload", async (ModelReq req, HttpContext context) =>
{
    try
    {
        var response = await Ollama(context).PostAsJsonAsync(
            "/api/generate",
            new { model = req.Model, keep_alive = 0 },
            context.RequestAborted);

        return response.IsSuccessStatusCode
            ? Results.Ok(new { unloaded = req.Model })
            : Results.Problem(
                await response.Content.ReadAsStringAsync(),
                statusCode: 502);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Pull model with streaming progress
app.MapPost("/api/models/pull", async (ModelReq req, HttpContext context) =>
{
    context.Response.ContentType = "text/event-stream";

    try
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/pull")
        {
            Content = JsonContent.Create(new
            {
                model = req.Model,
                stream = true
            })
        };

        using var response = await Ollama(context).SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(
            context.RequestAborted);

        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(
                context.RequestAborted);

            if (!string.IsNullOrWhiteSpace(line))
            {
                await context.Response.WriteAsync(
                    "data: " + line + "\n\n",
                    context.RequestAborted);

                await context.Response.Body.FlushAsync(
                    context.RequestAborted);
            }
        }

        await context.Response.WriteAsync(
            "event: done\ndata: {}\n\n",
            context.RequestAborted);
    }
    catch (Exception ex)
    {
        await context.Response.WriteAsync(
            "event: error\ndata: " +
            JsonSerializer.Serialize(new { error = ex.Message }) +
            "\n\n");
    }
});

// List conversations
app.MapGet("/api/conversations", async (StudioDb db) =>
    Results.Ok(
        await db.Conversations
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Model,
                x.Mode,
                x.UpdatedAt
            })
            .ToListAsync()));

// Create conversation
app.MapPost("/api/conversations", async (
    ConversationReq req,
    StudioDb db) =>
{
    var conversation = new Conversation
    {
        Title = string.IsNullOrWhiteSpace(req.Title)
            ? "New conversation"
            : req.Title,
        Model = req.Model,
        Mode = req.Mode ?? "chat"
    };

    db.Conversations.Add(conversation);
    await db.SaveChangesAsync();

    return Results.Ok(conversation);
});

// Get conversation messages
app.MapGet("/api/conversations/{id:int}/messages", async (
    int id,
    StudioDb db) =>
{
    return Results.Ok(
        await db.Messages
            .Where(x => x.ConversationId == id)
            .OrderBy(x => x.Id)
            .ToListAsync());
});

// Chat streaming
app.MapPost("/api/chat/stream", async (
    ChatReq req,
    HttpContext context,
    StudioDb db) =>
{
    context.Response.ContentType = "text/event-stream";

    if (string.IsNullOrWhiteSpace(req.Model) ||
        string.IsNullOrWhiteSpace(req.Message))
    {
        context.Response.StatusCode = 400;
        return;
    }

    var conversation = req.ConversationId.HasValue
        ? await db.Conversations.FindAsync(
            new object[] { req.ConversationId.Value },
            context.RequestAborted)
        : null;

    if (conversation == null)
    {
        conversation = new Conversation
        {
            Title = req.Message[..Math.Min(48, req.Message.Length)],
            Model = req.Model,
            Mode = req.Mode ?? "chat"
        };

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(context.RequestAborted);
    }

    db.Messages.Add(new ChatMessage
    {
        ConversationId = conversation.Id,
        Role = "user",
        Content = req.Message
    });

    conversation.Model = req.Model;
    conversation.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync(context.RequestAborted);

    // Retrieve latest 20 messages in chronological order
    var history = await db.Messages
        .Where(x => x.ConversationId == conversation.Id)
        .OrderByDescending(x => x.Id)
        .Take(20)
        .ToListAsync(context.RequestAborted);

    history.Reverse();

    var system = req.Mode == "code"
        ? "You are a senior software developer. Provide correct code with file names and explanations. Do not claim code was run."
        : req.Mode == "analysis"
            ? "You are a careful analyst. Structure assumptions, analysis, findings and limitations."
            : "You are a helpful assistant. Be accurate and concise.";

    var messages = new List<object>
    {
        new { role = "system", content = system }
    };

    messages.AddRange(
        history.Select(x => (object)new
        {
            role = x.Role,
            content = x.Content
        }));

    var payload = new
    {
        model = req.Model,
        stream = true,
        messages,
        options = new { num_ctx = 4096 }
    };

    var answer = "";

    try
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(payload)
        };

        using var response = await Ollama(context).SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(
            context.RequestAborted);

        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(
                context.RequestAborted);

            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                var token = content.GetString() ?? "";
                answer += token;

                await context.Response.WriteAsync(
                    "data: " +
                    JsonSerializer.Serialize(new
                    {
                        token,
                        conversationId = conversation.Id
                    }) +
                    "\n\n",
                    context.RequestAborted);

                await context.Response.Body.FlushAsync(
                    context.RequestAborted);
            }

            if (root.TryGetProperty("done", out var done) &&
                done.GetBoolean())
            {
                break;
            }
        }

        db.Messages.Add(new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = "assistant",
            Content = answer
        });

        conversation.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(context.RequestAborted);

        await context.Response.WriteAsync(
            "event: done\ndata: {}\n\n",
            context.RequestAborted);
    }
    catch (OperationCanceledException)
    {
        // Client disconnected or request was cancelled.
    }
    catch (Exception ex)
    {
        await context.Response.WriteAsync(
            "event: error\ndata: " +
            JsonSerializer.Serialize(new { error = ex.Message }) +
            "\n\n");
    }
});

// Angular route fallback: keep after all API routes
app.MapFallbackToFile("index.html");

app.Run();

// Request models
record ModelReq(string Model);

record ConversationReq(
    string? Title,
    string Model,
    string? Mode);

record ChatReq(
    string Model,
    string Message,
    int? ConversationId,
    string? Mode);

// Database entities
class Conversation
{
    public int Id { get; set; }
    public string Title { get; set; } = "New conversation";
    public string Model { get; set; } = "";
    public string Mode { get; set; } = "chat";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<ChatMessage> Messages { get; set; } = [];
}

class ChatMessage
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}

class StudioDb(DbContextOptions<StudioDb> options)
    : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Conversation>()
            .HasMany(x => x.Messages)
            .WithOne()
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
