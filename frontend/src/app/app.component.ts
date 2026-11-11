import {
  ChangeDetectorRef,
  Component,
  NgZone,
  OnInit,
  inject,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { HttpClient } from "@angular/common/http";

interface WorkspaceInfo {
  id: string;
  name: string;
  path: string;
}

interface WorkspaceDirectory {
  path: string;
  name: string;
}

interface WorkspaceDirectoryBrowser {
  currentPath: string | null;
  parentPath: string | null;
  directories: WorkspaceDirectory[];
}

interface WorkspaceEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number | null;
  lastModifiedUtc: string;
  children?: WorkspaceEntry[];
  expanded?: boolean;
  loading?: boolean;
}

@Component({
  selector: "app-root",
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: "./app.component.html",
})
export class AppComponent implements OnInit {
  private readonly apiBaseUrl = "/";
  private readonly zone = inject(NgZone);
  private readonly cd = inject(ChangeDetectorRef);
  http = inject(HttpClient);
  page = "chat";
  health = "checking";
  models: any[] = [];
  running: any[] = [];
  model = "";
  chats: any[] = [];
  conversationId: number | null = null;
  messages: any[] = [];
  prompt = "";
  codePrompt = "";
  code = "";
  language = "csharp";
  mode = "chat";
  busy = false;
  notice = "";
  abort?: AbortController;
  workspaces: WorkspaceInfo[] = [];
  activeWorkspace = "";
  get activeWorkspaceName() {
    return this.workspaces.find((workspace) => workspace.id === this.activeWorkspace)?.name ?? "";
  }
  fileTree: WorkspaceEntry[] = [];
  selectedFilePath = "";
  selectedFileContent = "";
  newWorkspaceName = "";
  newWorkspaceLocation = "";
  folderBrowserOpen = false;
  folderBrowserPath: string | null = null;
  folderBrowserParentPath: string | null = null;
  folderBrowserDirectories: WorkspaceDirectory[] = [];
  folderBrowserLoading = false;
  newFilePath = "";
  newFolderPath = "";
  private readonly newPaths = new Set<string>();
  private readonly renamedPaths = new Set<string>();

  ngOnInit() {
    this.refresh();
    this.loadWorkspaces();
  }
  onEnter(event: Event) {
    const keyboardEvent = event as KeyboardEvent;
    if (!keyboardEvent.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }
  refresh() {
    this.http.get<any>(`/api/health`).subscribe({
      next: (x) => (this.health = x.ollama),
      error: () => (this.health = "offline"),
    });
    this.loadModels();
    this.loadRunning();
    this.http
      .get<any[]>(`/api/conversations`)
      .subscribe((x) => (this.chats = x));
  }
  loadModels() {
    this.http.get<any>(`/api/models`).subscribe({
      next: (x) => {
        this.models = x.models || [];
        if (!this.model && this.models.length) this.model = this.models[0].name;
      },
      error: () => (this.notice = "Start Ollama to see installed models."),
    });
  }
  loadRunning() {
    this.http.get<any>(`/api/models/running`).subscribe({
      next: (x) => (this.running = x.models || []),
      error: () => (this.running = []),
    });
  }
  isRunning(n: string) {
    return this.running.some((x) => x.name === n);
  }
  load(n: string) {
    this.notice = "Loading model; CPU loading may take a while.";
    this.http.post<any>(`/api/models/load`, { model: n }).subscribe({
      next: () => {
        this.notice = n + " loaded";
        this.model = n;
        this.loadRunning();
      },
      error: (e) =>
        (this.notice = e.error?.detail || e.error?.title || "Load failed"),
    });
  }
  stop(n: string) {
    this.http.post<any>(`/api/models/unload`, { model: n }).subscribe({
      next: () => {
        this.notice = n + " stopped";
        this.loadRunning();
      },
      error: () => (this.notice = "Stop failed"),
    });
  }
  newChat() {
    this.messages = [];
    this.conversationId = null;
    this.page = "chat";
  }
  open(c: any) {
    this.conversationId = c.id;
    this.model = c.model;
    this.mode = c.mode;
    this.http
      .get<any[]>(`/api/conversations/${c.id}/messages`)
      .subscribe((x) => (this.messages = x));
    this.page = c.mode === "code" ? "code" : "chat";
  }
  async send(text = this.prompt, mode = this.mode) {
    if (!text.trim() || !this.model || this.busy) return;
    this.prompt = "";
    if (!this.conversationId) {
      const c: any = await new Promise((resolve, reject) =>
        this.http
          .post(`/api/conversations`, {
            title: "New conversation",
            model: this.model,
            mode,
          })
          .subscribe({ next: resolve, error: reject }),
      );
      this.conversationId = c.id;
    }
    this.messages = [
      ...this.messages,
      { role: "user", content: text },
      { role: "assistant", content: "" },
    ];
    const target = this.messages[this.messages.length - 1];
    this.zone.run(() => {
      this.busy = true;
      this.cd.detectChanges();
    });
    this.abort = new AbortController();
    try {
      const res = await fetch(`/api/chat/stream`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          model: this.model,
          message: text,
          conversationId: this.conversationId,
          mode,
        }),
        signal: this.abort.signal,
      });
      if (!res.ok) throw new Error(await res.text());
      const reader = res.body!.getReader(),
        decoder = new TextDecoder();
      let buf = "";
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        const lines = buf.split(/\r?\n/);
        buf = "";
        for (const rawLine of lines) {
          const line = rawLine.trim();
          if (!line) continue;
          if (line.startsWith("event:")) continue;
          if (!line.startsWith("data: ")) continue;
          const json = line.slice("data: ".length).trim();
          if (!json || json === "[DONE]") continue;
          const d = JSON.parse(json);
          const token = typeof d?.token === "string" ? d.token : "";
          if (!token) continue;
          this.zone.run(() => {
            target.content += token;
            this.messages = [...this.messages];
            this.cd.detectChanges();
          });
        }
      }
    } catch (e: any) {
      if (e.name !== "AbortError") {
        this.zone.run(() => {
          target.content += "\\n[Error] " + e.message;
          this.messages = [...this.messages];
          this.cd.detectChanges();
        });
      }
    } finally {
      this.zone.run(() => {
        this.busy = false;
        this.messages = [...this.messages];
        this.cd.detectChanges();
        this.refreshChats();
      });
    }
  }
  refreshChats() {
    this.http
      .get<any[]>(`/api/conversations`)
      .subscribe((x) => (this.chats = x));
  }
  stopGeneration() {
    this.abort?.abort();
    this.busy = false;
  }
  generate() {
    if (!this.codePrompt.trim()) return;
    this.page = "code";
    this.mode = "code";
    this.send(
      `Generate ${this.language} code for this requirement. Include file names, explanation and fenced code blocks.\\n\\n${this.codePrompt}`,
      "code",
    );
  }
  extract(s: string) {
    const m = s.match(/```(?:[\\w+#.-]+)?\\s*([\\s\\S]*?)```/);
    this.code = m ? m[1].trim() : s;
  }
  copy() {
    navigator.clipboard.writeText(this.code);
    this.notice = "Copied to clipboard";
  }
  download() {
    const a = document.createElement("a"),
      u = URL.createObjectURL(new Blob([this.code], { type: "text/plain" }));
    a.href = u;
    a.download =
      "generated." +
      (
        {
          csharp: "cs",
          typescript: "ts",
          python: "py",
          sql: "sql",
          dart: "dart",
        } as any
      )[this.language];
    a.click();
    URL.revokeObjectURL(u);
  }
  pull(name: string) {
    this.notice = "To download: run in PowerShell: ollama pull " + name;
  }

  loadWorkspaces(selectFirst = false) {
    this.http.get<WorkspaceInfo[]>("/api/workspaces").subscribe({
      next: (workspaces) => {
        this.workspaces = workspaces;
        if (selectFirst && !this.activeWorkspace && workspaces.length) {
          this.selectWorkspace(workspaces[0].id);
        }
      },
      error: (error) => this.showWorkspaceError("Loading workspaces", error),
    });
  }

  createWorkspace() {
    const name = this.newWorkspaceName.trim();
    if (!name) return;
    const location = this.newWorkspaceLocation.trim();

    this.http.post<WorkspaceInfo>("/api/workspaces", { name, location }).subscribe({
      next: (workspace) => {
        this.newWorkspaceName = "";
        this.newWorkspaceLocation = "";
        this.loadWorkspaces();
        this.selectWorkspace(workspace.id);
      },
      error: (error) => this.showWorkspaceError("Creating workspace", error),
    });
  }

  openFolderBrowser() {
    this.folderBrowserOpen = true;
    this.browseParentFolder(this.newWorkspaceLocation.trim() || null);
  }

  browseParentFolder(path: string | null) {
    this.folderBrowserLoading = true;
    this.http
      .get<WorkspaceDirectoryBrowser>("/api/workspaces/browse", {
        params: path ? { path } : {},
      })
      .subscribe({
        next: (result) => {
          this.folderBrowserPath = result.currentPath;
          this.folderBrowserParentPath = result.parentPath;
          this.folderBrowserDirectories = result.directories;
          this.folderBrowserLoading = false;
        },
        error: (error) => {
          this.folderBrowserLoading = false;
          this.showWorkspaceError("Browsing folders", error);
        },
      });
  }

  chooseParentFolder() {
    if (!this.folderBrowserPath) return;
    this.newWorkspaceLocation = this.folderBrowserPath;
    this.folderBrowserOpen = false;
  }

  selectWorkspace(id: string) {
    this.activeWorkspace = id;
    this.fileTree = [];
    this.selectedFilePath = "";
    this.selectedFileContent = "";
    this.newPaths.clear();
    this.renamedPaths.clear();
    this.loadDirectory();
  }

  refreshWorkspace() {
    if (!this.activeWorkspace) return;
    this.fileTree = [];
    this.selectedFilePath = "";
    this.selectedFileContent = "";
    this.loadDirectory();
  }

  toggleDirectory(entry: WorkspaceEntry) {
    entry.expanded = !entry.expanded;
    if (entry.expanded && !entry.children) {
      entry.loading = true;
      this.fetchDirectory(entry.path, (entries) => {
        entry.children = entries;
        entry.loading = false;
      }, () => {
        entry.loading = false;
      });
    }
  }

  openWorkspaceFile(entry: WorkspaceEntry) {
    this.selectedFilePath = entry.path;
    this.selectedFileContent = "";
    this.http
      .get<{ path: string; content: string }>(
        `/api/workspaces/${encodeURIComponent(this.activeWorkspace)}/files/content`,
        { params: { path: entry.path } },
      )
      .subscribe({
        next: (file) => (this.selectedFileContent = file.content),
        error: (error) => this.showWorkspaceError("Opening file", error),
      });
  }

  createWorkspaceFile() {
    const path = this.newFilePath.trim();
    if (!this.activeWorkspace || !path) return;

    this.http
      .post<WorkspaceEntry>(
        `/api/workspaces/${encodeURIComponent(this.activeWorkspace)}/files`,
        { path, content: "" },
      )
      .subscribe({
        next: (entry) => {
          this.newFilePath = "";
          this.newPaths.add(entry.path);
          this.refreshWorkspace();
          this.openWorkspaceFile(entry);
        },
        error: (error) => this.showWorkspaceError("Creating file", error),
      });
  }

  createWorkspaceDirectory() {
    const path = this.newFolderPath.trim();
    if (!this.activeWorkspace || !path) return;

    this.http
      .post<WorkspaceEntry>(
        `/api/workspaces/${encodeURIComponent(this.activeWorkspace)}/directories`,
        { path },
      )
      .subscribe({
        next: (entry) => {
          this.newFolderPath = "";
          this.newPaths.add(entry.path);
          this.refreshWorkspace();
        },
        error: (error) => this.showWorkspaceError("Creating folder", error),
      });
  }

  renameWorkspaceEntry(entry: WorkspaceEntry) {
    const newName = window.prompt("Enter the new name:", entry.name)?.trim();
    if (!newName || newName === entry.name) return;

    const kind = entry.isDirectory ? "directories" : "files";
    this.http
      .post<WorkspaceEntry>(
        `/api/workspaces/${encodeURIComponent(this.activeWorkspace)}/${kind}/rename`,
        { path: entry.path, newName },
      )
      .subscribe({
        next: (renamed) => {
          this.newPaths.delete(entry.path);
          this.renamedPaths.delete(entry.path);
          this.renamedPaths.add(renamed.path);
          if (
            this.selectedFilePath === entry.path ||
            (entry.isDirectory &&
              this.selectedFilePath.startsWith(`${entry.path}/`))
          ) {
            this.selectedFilePath = "";
            this.selectedFileContent = "";
          }
          this.refreshWorkspace();
        },
        error: (error) => this.showWorkspaceError("Renaming item", error),
      });
  }

  deleteWorkspaceEntry(entry: WorkspaceEntry) {
    const kind = entry.isDirectory ? "folder and all its contents" : "file";
    if (!window.confirm(`Permanently delete this ${kind} "${entry.name}"?`))
      return;

    const endpoint = entry.isDirectory ? "directories" : "files";
    this.http
      .delete(
        `/api/workspaces/${encodeURIComponent(this.activeWorkspace)}/${endpoint}`,
        { body: { path: entry.path, confirmed: true } },
      )
      .subscribe({
        next: () => {
          this.newPaths.delete(entry.path);
          this.renamedPaths.delete(entry.path);
          if (
            this.selectedFilePath === entry.path ||
            this.selectedFilePath.startsWith(`${entry.path}/`)
          ) {
            this.selectedFilePath = "";
            this.selectedFileContent = "";
          }
          this.refreshWorkspace();
        },
        error: (error) => this.showWorkspaceError("Deleting item", error),
      });
  }

  hasChangeIndicator(path: string) {
    return this.newPaths.has(path) || this.renamedPaths.has(path);
  }

  changeIndicator(path: string) {
    return this.newPaths.has(path) ? "New" : "Renamed";
  }

  private loadDirectory() {
    this.fetchDirectory("", (entries) => (this.fileTree = entries));
  }

  private fetchDirectory(
    path: string,
    onSuccess: (entries: WorkspaceEntry[]) => void,
    onError?: () => void,
  ) {
    this.http
      .get<WorkspaceEntry[]>(
        `/api/workspaces/${encodeURIComponent(this.activeWorkspace)}/files`,
        { params: { path } },
      )
      .subscribe({
        next: onSuccess,
        error: (error) => {
          onError?.();
          this.showWorkspaceError("Loading files", error);
        },
      });
  }

  private showWorkspaceError(action: string, error: any) {
    const message =
      error.error?.detail || error.error?.title || error.message || "Unknown error";
    this.notice = `${action}: ${message}`;
  }
}
