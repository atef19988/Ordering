import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { APP_CONFIG } from './core/config/app.config';

/** Root: the one-line header from the design system, then whatever the route renders. */
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="header">
      <h1 class="title"><a routerLink="/">Order console</a></h1>
      <nav class="nav" aria-label="Console">
        <a routerLink="/design" routerLinkActive="active" ariaCurrentWhenActive="page">Design reference</a>
      </nav>
      <!-- Task 10: replace with the live health indicator (db: healthy). -->
      <span class="meta">api: <span class="data">{{ apiBaseUrl }}</span></span>
    </header>
    <router-outlet />
  `,
  styles: `
    .header {
      display: flex;
      align-items: baseline;
      gap: var(--s-6);
      height: 49px;
      padding: 0 var(--s-4);
      border-bottom: 1px solid var(--c-line);
      line-height: 48px;
    }

    .title {
      font-size: var(--t-base);
      font-weight: 600;
    }

    .title a {
      text-decoration: none;
    }

    .nav {
      display: flex;
      gap: var(--s-4);
      font-size: var(--t-sm);
    }

    .nav a {
      color: var(--c-ink-muted);
      text-decoration: none;
    }

    .nav a.active {
      color: var(--c-ink);
      text-decoration: underline;
      text-underline-offset: 6px;
    }

    .meta {
      margin-left: auto;
      font-size: var(--t-sm);
      color: var(--c-ink-muted);
    }
  `,
})
export class App {
  protected readonly apiBaseUrl = inject(APP_CONFIG).apiBaseUrl;
}
