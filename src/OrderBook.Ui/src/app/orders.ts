import { Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';

@Component({
  selector: 'app-orders',
  imports: [FormsModule],
  template: `
    <h2 data-testid="page">Orders</h2>

    <input data-testid="symbol" [(ngModel)]="symbol" name="symbol" />
    <input data-testid="quantity" [(ngModel)]="quantity" name="quantity" />
    <button data-testid="place" (click)="place()">Place order</button>

    <p>Orders placed: <span data-testid="count">{{ count() }}</span></p>
    <!-- Refreshed without a navigation, so a test has to WAIT for it rather
         than assert on whatever the first render happened to contain. -->
    <button data-testid="refresh" (click)="load()">Refresh count</button>

    @if (reference()) {
      <p data-testid="reference">{{ reference() }}</p>
    }
    @if (error()) {
      <p data-testid="error">{{ error() }}</p>
    }
  `,
})
export class Orders {
  private http = inject(HttpClient);

  symbol = 'VOD';
  quantity = '100';

  count = signal(0);
  reference = signal('');
  error = signal('');

  constructor() {
    this.load();
  }

  load() {
    this.http.get<{ count: number }>('/api/orders').subscribe(r => this.count.set(r.count));
  }

  place() {
    this.reference.set('');
    this.error.set('');

    this.http.post<{ reference: string }>('/api/orders', {
      symbol: this.symbol,
      quantity: Number(this.quantity),
    }).subscribe({
      next: r => { this.reference.set(r.reference); this.load(); },
      // The domain's own message, thrown in whichever container hosts it,
      // carried through the API and rendered here.
      error: e => this.error.set(e.error?.error ?? 'request failed'),
    });
  }
}
