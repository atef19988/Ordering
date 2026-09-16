import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { PAGE_SIZES } from '../api/paging';
import { UiButtonComponent } from './ui-button.component';

const formatCount = new Intl.NumberFormat('en-GB');

/**
 * Previous / Next over a keyset cursor (there is no "jump to page 37"), a page-size select, and
 * `Page 3 · 50 rows of 12,340` when the total is known. The page change is announced politely.
 */
@Component({
  selector: 'ui-paginator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [UiButtonComponent],
  host: { role: 'navigation', 'aria-label': 'Pagination' },
  template: `
    <ui-button [disabled]="!hasPrev() || loading()" (pressed)="previous.emit()">Previous</ui-button>
    <ui-button [disabled]="!hasMore() || loading()" (pressed)="next.emit()">Next</ui-button>

    <span class="summary" aria-live="polite">{{ summary() }}</span>

    <label class="size">
      <span>Rows</span>
      <select
        class="control control--data"
        [value]="pageSize()"
        [disabled]="loading()"
        (change)="onSizeChange($event)"
      >
        @for (size of sizes; track size) {
          <option [value]="size" [selected]="size === pageSize()">{{ size }}</option>
        }
      </select>
    </label>
  `,
  styles: `
    :host {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--s-2) var(--s-3);
      font-size: var(--t-sm);
    }

    .summary {
      color: var(--c-ink-muted);
      font-variant-numeric: tabular-nums;
    }

    .size {
      display: inline-flex;
      align-items: center;
      gap: var(--s-2);
      margin-left: auto;
      color: var(--c-ink-muted);
    }

    .size .control {
      width: auto;
      min-height: 32px;
      padding: var(--s-1) var(--s-2);
    }
  `,
})
export class UiPaginatorComponent {
  /** Zero-based, as `BasePagedResource.pageIndex` keeps it. */
  readonly pageIndex = input.required<number>();
  /** Rows on the current page. */
  readonly count = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly hasPrev = input.required<boolean>();
  readonly hasMore = input.required<boolean>();
  readonly loading = input(false);
  readonly total = input<number | null>(null);

  readonly previous = output<void>();
  readonly next = output<void>();
  readonly pageSizeChange = output<number>();

  protected readonly sizes = PAGE_SIZES;

  protected readonly summary = computed(() => {
    const count = this.count();
    const rows = `${formatCount.format(count)} ${count === 1 ? 'row' : 'rows'}`;
    const total = this.total();
    const suffix = total === null ? '' : ` of ${formatCount.format(total)}`;
    return `Page ${formatCount.format(this.pageIndex() + 1)} · ${rows}${suffix}`;
  });

  protected onSizeChange(event: Event): void {
    this.pageSizeChange.emit(Number((event.target as HTMLSelectElement).value));
  }
}
