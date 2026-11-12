import { UsersComponent } from './users.component';

export const routes = [
  { path: 'users', component: UsersComponent },
  { path: 'card', loadComponent: () => import('./user-card.component').then(m => m.UserCardComponent) },
];

export function firstPath(): string {
  return routes[0].path;
}
