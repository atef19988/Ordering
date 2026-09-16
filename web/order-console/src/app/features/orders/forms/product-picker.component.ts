import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  forwardRef,
  inject,
  input,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { Subject, catchError, map, of, switchMap, tap } from 'rxjs';
import { UiComboboxComponent } from '../../../lib/ui/ui-combobox.component';
import { Product } from '../models/order.models';
import { CatalogueApi } from '../services/catalogue.api';

/**
 * A line's product control: `ui-combobox` over
 * `GET /api/products?search={typed}&inStock=true&pageSize=20`. The form sees only the selected
 * `code` (a `string`, or `null` while nothing is picked), so the line's validator is the plain
 * `required`. An empty input shows nothing — there is no "top 20 of everything" — and the
 * catalogue never arrives in more than one page of twenty.
 */
@Component({
  selector: 'app-product-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [UiComboboxComponent],
  providers: [
    { provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => ProductPickerComponent), multi: true },
  ],
  host: { '(focusout)': 'onTouched()', '(input)': 'onRawInput($event)' },
  template: `
    <ui-combobox
      [inputId]="id()"
      [options]="options()"
      [optionLabel]="label"
      [optionKey]="key"
      [loading]="searching()"
      [disabled]="disabled()"
      [data]="true"
      placeholder="Code or name starts with…"
      listLabel="Products"
      [selected]="selected()"
      (selectedChange)="onSelected($event)"
      (search)="lookups.next($event)"
    />
  `,
})
export class ProductPickerComponent implements ControlValueAccessor {
  /** The combobox input's id; the owning `ui-field` points at it. */
  readonly id = input.required<string>();

  protected readonly options = signal<readonly Product[]>([]);
  protected readonly searching = signal(false);
  protected readonly disabled = signal(false);
  protected readonly selected = signal<Product | null>(null);
  protected readonly lookups = new Subject<string>();

  protected readonly label = (product: Product): string =>
    product.name === ''
      ? product.code
      : `${product.code} — ${product.name} · ${product.availableQuantity} left`;
  protected readonly key = (product: Product): string => product.code;

  protected onChange: (code: string | null) => void = () => undefined;
  protected onTouched: () => void = () => undefined;

  constructor() {
    const api = inject(CatalogueApi);
    this.lookups
      .pipe(
        tap(() => this.searching.set(true)),
        // An empty text cancels whatever search was in flight and shows nothing.
        switchMap((text) =>
          text === ''
            ? of<readonly Product[]>([])
            : api.search(text).pipe(
                map((page) => page.items),
                // A failed search is an empty list, not a form error: the user simply types again.
                catchError(() => of<readonly Product[]>([])),
              ),
        ),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((items) => {
        this.options.set(items);
        this.searching.set(false);
      });
  }

  writeValue(code: string | null): void {
    if (!code) {
      this.selected.set(null);
      this.options.set([]);
      return;
    }
    if (this.selected()?.code !== code) {
      // Only a reset with a value reaches here; the code alone is all we know.
      this.selected.set({ code, name: '', price: 0, availableQuantity: 0 });
    }
  }

  registerOnChange(fn: (code: string | null) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(disabled: boolean): void {
    this.disabled.set(disabled);
  }

  protected onSelected(product: Product | null): void {
    this.selected.set(product);
    this.onChange(product?.code ?? null);
  }

  /** The combobox only reports text of one character or more; a cleared input must empty the list itself. */
  protected onRawInput(event: Event): void {
    const target = event.target;
    if (target instanceof HTMLInputElement && target.value.trim() === '') {
      this.lookups.next('');
    }
  }
}
