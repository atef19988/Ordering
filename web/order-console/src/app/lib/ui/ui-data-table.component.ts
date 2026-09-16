import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  computed,
  input,
  output,
} from '@angular/core';
import { Sort, SortDirection } from '../api/paging';
import { UiSpinnerComponent } from './ui-spinner.component';
import { UiColumn } from './ui-table.component';

/** A `ui-table` column that may also be sortable on the server and rendered by a template. */
export interface UiDataColumn<T> extends UiColumn<T> {
  readonly sortable?: boolean;
  /** Replaces `cell` for rich content; receives the row as `$implicit`. */
  readonly template?: TemplateRef<{ $implicit: T }>;
}

type AriaSort = 'ascending' | 'descending' | 'none';

/**
 * A server-paged table. It renders exactly what it is given — it never sorts or filters — and
 * reports a header press as `sortChange({ field, dir })` for the owner to send to the API.
 * Sticky header, `aria-sort` on the active column, a loading overlay that keeps the previous
 * rows visible, an `[slot=empty]` state, and a card layout under 600 px.
 */
@Component({
  selector: 'ui-data-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, UiSpinnerComponent],
  template: `
    <div class="frame" [attr.aria-busy]="loading() || null">
      <div class="scroll">
        <table>
          <caption [class.visually-hidden]="!showCaption()">{{ caption() }}</caption>
          <thead>
            <tr>
              @for (column of columns(); track column.field) {
                <th
                  scope="col"
                  [class.numeric]="column.numeric"
                  [class.sortable]="column.sortable"
                  [attr.aria-sort]="column.sortable ? ariaSort(column.field) : null"
                >
                  @if (column.sortable) {
                    <button type="button" class="sort" (click)="toggleSort(column.field)">
                      <span>{{ column.header }}</span>
                      <span class="arrow" aria-hidden="true">{{ arrow(column.field) }}</span>
                    </button>
                  } @else {
                    {{ column.header }}
                  }
                </th>
              }
            </tr>
          </thead>
          <tbody>
            @for (row of rows(); track rowKey()(row, $index)) {
              <tr>
                @for (column of columns(); track column.field) {
                  <td
                    [class.data]="column.data || column.numeric"
                    [class.numeric]="column.numeric"
                    [attr.data-label]="column.header"
                  >
                    @if (column.template; as template) {
                      <ng-container *ngTemplateOutlet="template; context: { $implicit: row }" />
                    } @else {
                      {{ column.cell(row) }}
                    }
                  </td>
                }
              </tr>
            }
          </tbody>
        </table>

        @if (rows().length === 0 && !loading()) {
          <div class="empty">
            <ng-content select="[slot=empty]">
              <p class="muted">{{ empty() }}</p>
            </ng-content>
          </div>
        }
      </div>

      @if (loading()) {
        <div class="overlay">
          <ui-spinner size="md" label="Loading rows" [showLabel]="rows().length === 0" />
        </div>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
    }

    .frame {
      position: relative;
    }

    .scroll {
      max-height: var(--table-max-height, 60vh);
      overflow: auto;
      border: 1px solid var(--c-line);
      border-radius: var(--r-sm);
    }

    table {
      width: 100%;
      border-collapse: separate;
      border-spacing: 0;
      font-size: var(--t-sm);
    }

    caption {
      text-align: left;
      padding: var(--s-2) var(--s-3);
      font-size: var(--t-xs);
      font-weight: 600;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: var(--c-ink-muted);
    }

    th,
    td {
      padding: var(--s-2) var(--s-3);
      text-align: left;
      vertical-align: baseline;
      border-bottom: 1px solid var(--c-line);
    }

    th {
      position: sticky;
      top: 0;
      z-index: 1;
      background: var(--c-panel);
      font-size: var(--t-xs);
      font-weight: 600;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: var(--c-ink-muted);
      white-space: nowrap;
    }

    .sort {
      display: inline-flex;
      align-items: center;
      gap: var(--s-1);
      padding: 0;
      border: 0;
      background: transparent;
      font: inherit;
      letter-spacing: inherit;
      text-transform: inherit;
      color: inherit;
      cursor: pointer;
    }

    th[aria-sort='ascending'] .sort,
    th[aria-sort='descending'] .sort {
      color: var(--c-ink);
    }

    .arrow {
      display: inline-block;
      min-width: 1ch;
    }

    .numeric,
    .numeric .sort {
      text-align: right;
      justify-content: flex-end;
    }

    .numeric .sort {
      width: 100%;
    }

    .data {
      font-family: var(--f-data);
      font-variant-numeric: tabular-nums;
    }

    tbody tr:last-child td {
      border-bottom: 0;
    }

    .empty {
      padding: var(--s-6) var(--s-3);
    }

    .muted {
      color: var(--c-ink-muted);
    }

    .overlay {
      position: absolute;
      inset: 0;
      display: flex;
      align-items: center;
      justify-content: center;
      background: rgb(255 255 255 / 0.6);
    }

    @media (max-width: 599px) {
      thead {
        position: absolute;
        width: 1px;
        height: 1px;
        overflow: hidden;
        clip: rect(0 0 0 0);
      }

      tr {
        display: block;
        padding: var(--s-2) 0;
        border-bottom: 1px solid var(--c-line);
      }

      tbody tr:last-child {
        border-bottom: 0;
      }

      td,
      td.numeric {
        display: flex;
        justify-content: space-between;
        gap: var(--s-4);
        border-bottom: 0;
        padding: var(--s-1) var(--s-3);
      }

      td::before {
        content: attr(data-label);
        font-family: var(--f-ui);
        font-size: var(--t-xs);
        font-weight: 600;
        letter-spacing: 0.04em;
        text-transform: uppercase;
        color: var(--c-ink-muted);
      }
    }
  `,
})
export class UiDataTableComponent<T> {
  readonly columns = input.required<readonly UiDataColumn<T>[]>();
  readonly rows = input.required<readonly T[]>();
  readonly caption = input.required<string>();
  readonly showCaption = input(false);
  readonly sort = input<Sort | null>(null);
  readonly loading = input(false);
  readonly empty = input('Nothing to show.');
  readonly rowKey = input<(row: T, index: number) => string | number>((_, index) => index);

  readonly sortChange = output<Sort>();

  private readonly activeField = computed(() => this.sort()?.field ?? null);

  protected ariaSort(field: string): AriaSort {
    const sort = this.sort();
    if (sort === null || sort.field !== field) {
      return 'none';
    }
    return sort.dir === 'desc' ? 'descending' : 'ascending';
  }

  protected arrow(field: string): string {
    const sort = this.sort();
    if (sort === null || sort.field !== field) {
      return '';
    }
    return sort.dir === 'desc' ? '↓' : '↑';
  }

  /** Press the active header to flip the direction; press another to sort by it ascending. */
  protected toggleSort(field: string): void {
    const dir: SortDirection =
      this.activeField() === field && this.sort()?.dir === 'asc' ? 'desc' : 'asc';
    this.sortChange.emit({ field, dir });
  }
}
