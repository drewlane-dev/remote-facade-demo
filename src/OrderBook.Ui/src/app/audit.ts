import { Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

@Component({
  selector: 'app-audit',
  template: `
    <h2 data-testid="page">Audit</h2>
    <ul>
      @for (entry of entries(); track entry) {
        <li data-testid="entry">{{ entry }}</li>
      } @empty {
        <li data-testid="empty">nothing yet</li>
      }
    </ul>
  `,
})
export class Audit {
  private http = inject(HttpClient);
  entries = signal<string[]>([]);

  constructor() {
    this.http.get<string[]>('/api/audit').subscribe(e => this.entries.set(e));
  }
}
