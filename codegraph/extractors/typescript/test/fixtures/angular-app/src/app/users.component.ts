import { Component } from '@angular/core';
import { User } from './user';
import { UserCardComponent } from './user-card.component';

@Component({
  selector: 'app-users',
  templateUrl: './users.component.html',
  imports: [UserCardComponent],
})
export class UsersComponent {
  users: User[] = [];
  selected?: User;

  select(user: User): void {
    this.selected = user;
  }
}
