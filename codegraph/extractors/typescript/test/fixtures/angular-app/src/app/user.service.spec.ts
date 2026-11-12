import { HttpService } from './http.service';
import { UserService } from './user.service';

describe('UserService', () => {
  let service: UserService;

  beforeEach(() => {
    service = new UserService(new HttpService());
  });

  it('formats names', () => {
    expect(service.formatName({ id: 1, name: ' ada ' })).toBe('ADA');
  });

  it.skip('loads', async () => {
    await service.load(1);
  });
});
