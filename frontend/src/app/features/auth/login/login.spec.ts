import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { Auth } from '../../../core/auth/auth';
import { ApiError } from '../../../core/http/api-error';
import { Login } from './login';

describe('Login', () => {
  let login: ReturnType<typeof vi.fn>;
  let navigateByUrl: ReturnType<typeof vi.spyOn>;

  async function render(returnUrl?: string) {
    login = vi.fn();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: Auth, useValue: { login } }],
    });
    navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(Login);
    if (returnUrl) {
      fixture.componentRef.setInput('returnUrl', returnUrl);
    }
    await fixture.whenStable();
    return fixture;
  }

  async function submit(
    fixture: Awaited<ReturnType<typeof render>>,
    username: string,
    password: string,
  ) {
    const element = fixture.nativeElement as HTMLElement;
    for (const [id, value] of [
      ['username', username],
      ['password', password],
    ]) {
      const input = element.querySelector<HTMLInputElement>(`#${id}`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
  }

  it('signs in and goes back to the page the user wanted', async () => {
    const fixture = await render('/products?page=2');
    login.mockReturnValue(of(undefined));

    await submit(fixture, ' manager ', 'manager');

    expect(login).toHaveBeenCalledWith('manager', 'manager');
    expect(navigateByUrl).toHaveBeenCalledWith('/products?page=2');
  });

  it('never redirects to another site after sign-in', async () => {
    const fixture = await render('//evil.example.com');
    login.mockReturnValue(of(undefined));

    await submit(fixture, 'manager', 'manager');

    expect(navigateByUrl).toHaveBeenCalledWith('/products');
  });

  it.each([
    [401, 'Invalid username or password.'],
    [429, 'Too many sign-in attempts'],
  ])('explains a %i from the token endpoint', async (status, message) => {
    const fixture = await render();
    login.mockReturnValue(throwError(() => new ApiError(status, 'x', 'server message')));

    await submit(fixture, 'manager', 'wrong');

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[role=alert]')!.textContent,
    ).toContain(message);
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('does not call the API with empty fields', async () => {
    const fixture = await render();

    await submit(fixture, '', '');

    expect(login).not.toHaveBeenCalled();
  });
});
