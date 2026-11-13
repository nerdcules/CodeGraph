import Shapes = require('./shapes');
var square = new Shapes.Square(2);
export function run(): string { return Shapes.describe(square); }
