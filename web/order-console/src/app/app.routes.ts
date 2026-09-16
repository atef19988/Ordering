import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./features/orders/orders.page').then((m) => m.OrdersPage),
    title: 'Order console',
  },
  {
    path: 'design',
    loadComponent: () =>
      import('./features/design/design-reference.component').then(
        (m) => m.DesignReferenceComponent,
      ),
    title: 'Design reference — Order console',
  },
  { path: '**', redirectTo: '' },
];
