/**
 * The single status → colour + label map from docs/design-system.md. `ui-badge` and
 * `ui-status-band` read it; nobody else decides what colour a state is. Every tone has a word,
 * because colour is never the only signal.
 */
export type StatusTone = 'ok' | 'wait' | 'stop' | 'void';

export interface StatusView {
  readonly tone: StatusTone;
  readonly label: string;
}

/** Keys are the API's own vocabulary (`OrderDetailDto.status`, `.notificationStatus`) plus the two client-side outcomes. */
const STATUS_VIEWS: Readonly<Record<string, StatusView>> = {
  Confirmed: { tone: 'ok', label: 'Confirmed' },
  Sent: { tone: 'ok', label: 'Notified' },
  Pending: { tone: 'wait', label: 'Notification pending' },
  Retrying: { tone: 'wait', label: 'Notification pending' },
  Failed: { tone: 'stop', label: 'Notification failed' },
  InsufficientStock: { tone: 'stop', label: 'Not enough stock' },
  Cancelled: { tone: 'void', label: 'Cancelled' },
  None: { tone: 'void', label: 'No notification' },
};

/** Unknown states are shown verbatim in the neutral tone rather than hidden or guessed. */
export function statusView(status: string): StatusView {
  return STATUS_VIEWS[status] ?? { tone: 'void', label: status };
}

export const KNOWN_STATUSES: readonly string[] = Object.keys(STATUS_VIEWS);
