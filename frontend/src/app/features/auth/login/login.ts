import { Component, inject, input, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Auth } from '../../../core/auth/auth';
import { ApiError } from '../../../core/http/api-error';
import { Icon } from '../../../shared/ui';

/**
 * The sign-in page: a username/password form that calls {@link Auth.login}, then goes back to the page the user was
 * trying to open (or to the products page). The demo accounts are listed under the form.
 */
@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, Icon],
  templateUrl: './login.html',
})
export class Login {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);

  /**
   * Where to go after signing in: the `?returnUrl=` query parameter set by the auth guard. An `input()` is a value the
   * component receives from outside; here the router fills it from the URL (`withComponentInputBinding`, app.config.ts).
   * A missing parameter arrives as undefined.
   */
  readonly returnUrl = input<string | undefined>();

  // Signals: values the template re-reads automatically when they change.
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
        this.errorMessage.set(signInErrorMessage(error));
      },
    });
  }

  /** Only same-app paths: never redirect to another site after sign-in. */
  private safeReturnUrl(): string {
    const url = this.returnUrl();
    return url?.startsWith('/') && !url.startsWith('//') ? url : '/products';
  }
}

function signInErrorMessage(error: ApiError): string {
  switch (error.status) {
    case 401:
      return 'Invalid username or password.';
    case 429:
      return 'Too many sign-in attempts. Please wait a minute and try again.';
    default:
      return error.message;
  }
}
