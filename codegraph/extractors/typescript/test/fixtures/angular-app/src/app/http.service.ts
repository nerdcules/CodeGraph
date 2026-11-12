import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class HttpService {
  get<T>(url: string): Promise<T> {
    return fetch(url).then(response => response.json() as Promise<T>);
  }
}
