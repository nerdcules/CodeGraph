import { Pipe } from '@angular/core';

@Pipe({ name: 'shout' })
export class ShoutPipe {
  transform(value: string): string {
    return `${value}!`;
  }
}
