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
  ngOnInit() {
    this.refresh();
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
}
