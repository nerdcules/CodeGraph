namespace Shapes {
  export interface Shape { area(): number; }
  export class Square implements Shape {
    constructor(private side: number) { }
    area(): number { return this.side * this.side; }
  }
  export function describe(shape: Shape): string { return 'area ' + shape.area(); }
}
export = Shapes;
