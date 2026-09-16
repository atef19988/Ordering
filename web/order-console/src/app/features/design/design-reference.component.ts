import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { KNOWN_STATUSES } from '../../lib/ui/status';
import { UiAlertComponent } from '../../lib/ui/ui-alert.component';
import { UiBadgeComponent } from '../../lib/ui/ui-badge.component';
import { UiButtonComponent } from '../../lib/ui/ui-button.component';
import { UiFieldComponent } from '../../lib/ui/ui-field.component';
import { UiSpinnerComponent } from '../../lib/ui/ui-spinner.component';
import { UiStatusBandComponent } from '../../lib/ui/ui-status-band.component';
import { UiColumn, UiTableComponent } from '../../lib/ui/ui-table.component';
import { DemoCatalogueComponent } from './demo-catalogue.component';
import { DemoFormComponent } from './demo-form.component';

interface DemoLine {
  readonly productCode: string;
  readonly quantity: number;
  readonly unitPrice: number;
  readonly lineTotal: number;
}

interface BandState {
  readonly status: string;
  readonly notification: string;
  readonly attempts: number;
}

const BAND_STATES: readonly BandState[] = [
  { status: 'Confirmed', notification: 'Pending', attempts: 0 },
  { status: 'Confirmed', notification: 'Pending', attempts: 2 },
  { status: 'Confirmed', notification: 'Sent', attempts: 3 },
  { status: 'Confirmed', notification: 'Failed', attempts: 5 },
  { status: 'Cancelled', notification: 'Sent', attempts: 1 },
];

const money = (value: number): string => value.toFixed(2);

/**
 * The design reference: every `ui-*` component in every state, plus the tokens, on one page.
 * Change a component and this page is where the change is judged.
 */
@Component({
  selector: 'app-design-reference',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    UiButtonComponent,
    UiFieldComponent,
    UiTableComponent,
    UiBadgeComponent,
    UiStatusBandComponent,
    UiAlertComponent,
    UiSpinnerComponent,
    DemoFormComponent,
    DemoCatalogueComponent,
  ],
  templateUrl: './design-reference.component.html',
  styleUrl: './design-reference.component.scss',
})
export class DesignReferenceComponent {
  protected readonly colours = [
    ['--c-ink', 'ink — text'],
    ['--c-ink-muted', 'ink-muted — secondary text'],
    ['--c-line', 'line — borders'],
    ['--c-surface', 'surface — page'],
    ['--c-panel', 'panel — action column'],
    ['--c-ok', 'ok — confirmed, sent'],
    ['--c-wait', 'wait — pending, retrying'],
    ['--c-stop', 'stop — conflict, failed'],
    ['--c-void', 'void — cancelled'],
  ] as const;

  protected readonly typeScale = ['xs', 'sm', 'base', 'lg', 'xl', '2xl'] as const;
  protected readonly spacing = ['1', '2', '3', '4', '6', '8', '12'] as const;

  protected readonly statuses = KNOWN_STATUSES;

  protected readonly lines: readonly DemoLine[] = [
    { productCode: 'SKU-001', quantity: 2, unitPrice: 12.5, lineTotal: 25 },
    { productCode: 'SKU-002', quantity: 1, unitPrice: 4, lineTotal: 4 },
  ];

  protected readonly lineColumns: readonly UiColumn<DemoLine>[] = [
    { field: 'code', header: 'Code', data: true, cell: (l) => l.productCode },
    { field: 'qty', header: 'Qty', numeric: true, cell: (l) => String(l.quantity) },
    { field: 'unit', header: 'Unit', numeric: true, cell: (l) => money(l.unitPrice) },
    { field: 'amount', header: 'Amount', numeric: true, cell: (l) => money(l.lineTotal) },
  ];

  protected readonly linesFooter = ['total', '', '', money(29)];

  protected readonly bandStates = BAND_STATES;
  protected readonly bandIndex = signal(0);
  protected readonly band = () => BAND_STATES[this.bandIndex()];

  protected readonly busy = signal(false);
  protected readonly alertVisible = signal(true);

  protected nextBand(): void {
    this.bandIndex.update((i) => (i + 1) % BAND_STATES.length);
  }

  protected toggleBusy(): void {
    this.busy.set(true);
    setTimeout(() => this.busy.set(false), 1500);
  }
}
