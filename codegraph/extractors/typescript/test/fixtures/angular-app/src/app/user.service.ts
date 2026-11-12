import { Injectable } from '@angular/core';
import { HttpService } from './http.service';
import { Loader } from './loader';
import { Repository } from './repository';
import { User } from './user';

/** Loads users from the server. */
@Injectable({ providedIn: 'root' })
export class UserService extends Repository<User> implements Loader {
  constructor(private readonly http: HttpService) {
    super();
  }

  override async load(id: number): Promise<User> {
    this.log(`loading user ${id}`);
    return this.http.get<User>(`/users/${id}`);
  }

  formatName(user: User): string {
    return user.name.trim().toUpperCase();
  }

  #secret(): number {
    return Math.max(1, 2);
  }
}
