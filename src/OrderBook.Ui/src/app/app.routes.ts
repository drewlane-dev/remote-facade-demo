import { Routes } from '@angular/router';
import { Orders } from './orders';
import { Audit } from './audit';

// Two routes, so a browser test has real navigation to exercise rather than a
// single page pretending to be an app.
export const routes: Routes = [
  { path: '', component: Orders },
  { path: 'audit', component: Audit },
];
