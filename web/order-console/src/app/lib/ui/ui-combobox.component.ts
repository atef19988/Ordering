import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  TemplateRef,
  afterRenderEffect,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  model,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { Subject, debounceTime, filter } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { UiSpinnerComponent } from './ui-spinner.component';

/**
 * The WAI-ARIA 1.2 combobox pattern with a listbox popup, as a typeahead. The input keeps focus
 * the whole time; the highlighted option is announced through `aria-activedescendant`. Typing
 * emits `search` (debounced 300 ms, at least one character); the owner answers with `options`
 * and `loading`. `selected` is the truth: text that was never selected reverts on blur. Knows
 * nothing about products or HTTP.
 *
 * Keyboard: ↓ opens/moves down, ↑ moves up, Home/End jump, Enter selects, Escape closes.
 */
@Component({
  selector: 'ui-combobox',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, UiSpinnerComponent],
  template: `
    <div class="box">
      <input
        [id]="inputId()"
        class="control"
        [class.control--data]="data()"
        type="text"
        role="combobox"
        autocomplete="off"
        spellcheck="false"
        aria-autocomplete="list"
        aria-haspopup="listbox"
        [attr.aria-expanded]="open()"
        [attr.aria-controls]="listboxId()"
        [attr.aria-activedescendant]="activeId()"
        [placeholder]="placeholder()"
        [disabled]="disabled()"
        [value]="text()"
        (input)="onInput($event)"
        (keydown)="onKeydown($event)"
        (click)="openIfPossible()"
        (blur)="onBlur()"
      />
      @if (loading()) {
        <ui-spinner class="spin" label="Searching" />
      }

      <!-- Options are not tab stops by design: focus stays in the input (ARIA 1.2 combobox). -->
      <!-- eslint-disable @angular-eslint/template/click-events-have-key-events, @angular-eslint/template/interactive-supports-focus -->
      <ul
        #listbox
        [id]="listboxId()"
        class="listbox"
        role="listbox"
        [attr.aria-label]="listLabel()"
        [hidden]="!open()"
      >
        @for (option of options(); track optionKey()(option); let i = $index) {
          <li
            [id]="optionId(i)"
            role="option"
            [attr.aria-selected]="i === activeIndex()"
            [class.active]="i === activeIndex()"
            (mousedown)="$event.preventDefault()"
            (mousemove)="activeIndex.set(i)"
            (click)="select(option)"
          >
            @if (optionTemplate(); as template) {
              <ng-container *ngTemplateOutlet="template; context: { $implicit: option }" />
            } @else {
              {{ optionLabel()(option) }}
            }
          </li>
        } @empty {
          @if (showNoMatches()) {
            <li role="option" aria-disabled="true" aria-selected="false" class="none">No matches</li>
          }
        }
      </ul>
      <!-- eslint-enable -->
    </div>
  `,
  styles: `
    :host {
      display: block;
    }

    .box {
      position: relative;
    }

    .spin {
      position: absolute;
      top: 50%;
      right: var(--s-3);
      transform: translateY(-50%);
    }

    .listbox {
      position: absolute;
      top: calc(100% + 2px);
      left: 0;
      right: 0;
      z-index: 10;
      max-height: 280px;
      margin: 0;
      padding: var(--s-1) 0;
      overflow-y: auto;
      list-style: none;
      background: var(--c-surface);
      border: 1px solid var(--c-ink);
      border-radius: var(--r-sm);
      font-size: var(--t-sm);
    }

    li {
      padding: var(--s-2) var(--s-3);
      cursor: pointer;
    }

    li.active {
      background: var(--c-panel);
      box-shadow: inset 3px 0 0 var(--c-ink);
    }

    li.none {
      color: var(--c-ink-muted);
      cursor: default;
    }
  `,
})
export class UiComboboxComponent<T> {
  /** The input's id; a `ui-field` label points at it. (Not `id`, which would also land on the host.) */
  readonly inputId = input.required<string>();
  readonly options = input.required<readonly T[]>();
  readonly optionLabel = input.required<(option: T) => string>();
  /** Stable identity per option for tracking and option ids; defaults to the label. */
  readonly optionKey = input<(option: T) => string>((option) => this.optionLabel()(option));
  readonly optionTemplate = input<TemplateRef<{ $implicit: T }> | null>(null);
  readonly loading = input(false);
  readonly disabled = input(false);
  readonly placeholder = input('');
  /** Monospace input, for codes. */
  readonly data = input(false);
  readonly listLabel = input('Matches');
  readonly minChars = input(1);

  readonly selected = model<T | null>(null);
  /** Debounced 300 ms, only when the text has at least `minChars` characters. */
  // The spec names this `search`; the host is a custom element, so it cannot shadow the native event.
  // eslint-disable-next-line @angular-eslint/no-output-native
  readonly search = output<string>();

  protected readonly text = linkedSignal(() => {
    const selected = this.selected();
    return selected === null ? '' : this.optionLabel()(selected);
  });
  protected readonly open = signal(false);
  protected readonly activeIndex = signal(-1);

  protected readonly listboxId = computed(() => `${this.inputId()}-listbox`);
  protected readonly activeId = computed(() =>
    this.open() && this.activeIndex() >= 0 ? this.optionId(this.activeIndex()) : null,
  );
  protected readonly showNoMatches = computed(
    () => !this.loading() && this.text().trim().length >= this.minChars(),
  );

  private readonly listbox = viewChild.required<ElementRef<HTMLUListElement>>('listbox');
  private readonly searchText = new Subject<string>();

  constructor() {
    // No distinctUntilChanged: after a reset the same text must search again, and the debounce
    // already turns a burst of keystrokes into one request.
    this.searchText
      .pipe(
        debounceTime(300),
        filter((text) => text.length >= this.minChars()),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((text) => this.search.emit(text));

    // New options while open: keep the highlight in range.
    effect(() => {
      const count = this.options().length;
      if (this.activeIndex() >= count) {
        this.activeIndex.set(count - 1);
      }
    });

    afterRenderEffect(() => {
      const index = this.activeIndex();
      if (index < 0 || !this.open()) {
        return;
      }
      this.listbox()
        .nativeElement.querySelector(`#${CSS.escape(this.optionId(index))}`)
        ?.scrollIntoView({ block: 'nearest' });
    });
  }

  protected optionId(index: number): string {
    return `${this.inputId()}-option-${index}`;
  }

  protected onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.text.set(value);
    const selected = this.selected();
    if (selected !== null && this.optionLabel()(selected) !== value) {
      this.selected.set(null);
    }
    this.open.set(true);
    this.activeIndex.set(-1);
    this.searchText.next(value.trim());
  }

  protected onKeydown(event: KeyboardEvent): void {
    const count = this.options().length;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        if (!this.open()) {
          this.open.set(true);
          this.activeIndex.set(count > 0 ? 0 : -1);
        } else if (count > 0) {
          this.activeIndex.set(Math.min(this.activeIndex() + 1, count - 1));
        }
        return;
      case 'ArrowUp':
        event.preventDefault();
        if (this.open() && count > 0) {
          this.activeIndex.set(Math.max(this.activeIndex() - 1, 0));
        }
        return;
      case 'Home':
        if (this.open() && count > 0) {
          event.preventDefault();
          this.activeIndex.set(0);
        }
        return;
      case 'End':
        if (this.open() && count > 0) {
          event.preventDefault();
          this.activeIndex.set(count - 1);
        }
        return;
      case 'Enter': {
        const option = this.options()[this.activeIndex()];
        if (this.open() && option !== undefined) {
          event.preventDefault();
          this.select(option);
        }
        return;
      }
      case 'Escape':
        if (this.open()) {
          event.preventDefault();
          this.close();
        }
        return;
      case 'Tab':
        this.close();
        return;
      default:
        return;
    }
  }

  protected openIfPossible(): void {
    if (!this.disabled() && (this.options().length > 0 || this.showNoMatches())) {
      this.open.set(true);
    }
  }

  protected onBlur(): void {
    this.close();
    // The selection is the truth; typed text that never became a selection is dropped.
    const selected = this.selected();
    this.text.set(selected === null ? '' : this.optionLabel()(selected));
  }

  protected select(option: T): void {
    this.selected.set(option);
    this.text.set(this.optionLabel()(option));
    this.close();
  }

  private close(): void {
    this.open.set(false);
    this.activeIndex.set(-1);
  }
}
