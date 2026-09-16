import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** One column: a header, how to render a row into text, and whether it is data (mono, right-aligned). */
export interface UiColumn<T> {
  readonly field: string;
  readonly header: string;
  readonly cell: (row: T) => string;
  /** Data columns (codes, quantities, money) use `--f-data`; `numeric` also right-aligns. */
  readonly data?: boolean;
  readonly numeric?: boolean;
}

/**
 * A dense, left-aligned data table. Numbers sit right-aligned in monospace so two rows can be
 * compared by eye; the caption is required for assistive technology and may be hidden visually.
 */
@Component({
  selector: 'ui-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="scroll">
      <table>
        <caption [class.visually-hidden]="!showCaption()">{{ caption() }}</caption>
        <thead>
          <tr>
            @for (column of columns(); track column.field) {
              <th scope="col" [class.numeric]="column.numeric">{{ column.header }}</th>
            }
          </tr>
        </thead>
        <tbody>
          @for (row of rows(); track rowKey()(row, $index)) {
            <tr>
              @for (column of columns(); track column.field) {
                <td [class.data]="column.data || column.numeric" [class.numeric]="column.numeric">
                  {{ column.cell(row) }}
                </td>
              }
            </tr>
          } @empty {
            <tr>
              <td class="empty" [attr.colspan]="columns().length">{{ empty() }}</td>
            </tr>
          }
        </tbody>
        @if (footer(); as footer) {
          <tfoot>
            <tr>
              @for (cell of footer; track $index; let i = $index) {
                <td
                  [class.data]="columns()[i]?.data || columns()[i]?.numeric"
                  [class.numeric]="columns()[i]?.numeric"
                >
                  {{ cell }}
                </td>
              }
            </tr>
          </tfoot>
        }
      </table>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }

    .scroll {
      overflow-x: auto;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--t-sm);
    }

    caption {
      text-align: left;
      padding-bottom: var(--s-2);
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
      font-size: var(--t-xs);
      font-weight: 600;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: var(--c-ink-muted);
      white-space: nowrap;
    }

    .numeric {
      text-align: right;
    }

    .data {
      font-family: var(--f-data);
      font-variant-numeric: tabular-nums;
    }

    .empty {
      color: var(--c-ink-muted);
    }

    tfoot td {
      border-bottom: 0;
      border-top: 2px solid var(--c-ink);
      font-weight: 600;
    }
  `,
})
export class UiTableComponent<T> {
  readonly columns = input.required<readonly UiColumn<T>[]>();
  readonly rows = input.required<readonly T[]>();
  readonly caption = input.required<string>();
  readonly showCaption = input(false);
  readonly empty = input('Nothing to show.');
  /** One cell per column for a totals row; `''` leaves a cell blank. */
  readonly footer = input<readonly string[] | null>(null);
  readonly rowKey = input<(row: T, index: number) => string | number>((_, index) => index);
}
