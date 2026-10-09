import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { fakeAuth } from '../testing/fake-auth';
import { App } from './app';

describe('App', () => {
  it('renders the application shell', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), fakeAuth()] });
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('header')?.textContent).toContain('Product Management');
    expect(element.querySelector('router-outlet')).not.toBeNull();
    expect(element.querySelector('header')?.textContent).toContain('test-user');
    expect(element.querySelector('header')?.textContent).toContain('Sign out');
  });
});
