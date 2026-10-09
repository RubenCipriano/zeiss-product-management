import { Component, inject, input, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Auth } from '../../../core/auth/auth';
import { ApiError } from '../../../core/http/api-error';
import { Icon } from '../../../shared/ui';

/** Sign-in with the credentials of a registered API consumer. */
@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, Icon],
  templateUrl: './login.html',
})
export class Login {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);

  /** Where to go after signing in (set by the auth guard; bound from the query string). */
  /** From the `returnUrl` query parameter. The router binds a missing parameter as undefined, not as a default. */
  readonly returnUrl = input<string | undefined>();

  protected readonly signingIn = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly form = new FormGroup({
    username: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  protected submit(): void {
    if (this.form.invalid || this.signingIn()) {
      this.form.markAllAsTouched();
      return;
    }

    const { username, password } = this.form.getRawValue();
    this.signingIn.set(true);
    this.errorMessage.set(null);

    this.auth.login(username.trim(), password).subscribe({
      next: () => void this.router.navigateByUrl(this.safeReturnUrl()),
      error: (error: ApiError) => {
        this.signingIn.set(false);
        this.errorMessage.set(
          error.status === 401
            ? 'Invalid username or password.'
            : error.status === 429
              ? 'Too many sign-in attempts. Please wait a minute and try again.'
              : error.message,
        );
      },
    });
  }

  /** Only same-app paths: never redirect to another site after sign-in. */
  private safeReturnUrl(): string {
    const url = this.returnUrl();
    return url?.startsWith('/') && !url.startsWith('//') ? url : '/products';
  }
}
