import { Component, inject } from '@angular/core';
import { ShoutPipe } from './shout.pipe';
import { User } from './user';
import { UserService } from './user.service';

@Component({
  selector: 'app-user-card',
  imports: [ShoutPipe],
  template: '<span (click)="pick()">{{ display() }} {{ user.name | shout }}</span>',
})
export class UserCardComponent {
  user!: User;
  private readonly service = inject(UserService);

  display(): string {
    return this.service.formatName(this.user);
  }

  pick = () => this.display();

  ngOnInit(): void {}

  private unusedHelper(): void {}
}
