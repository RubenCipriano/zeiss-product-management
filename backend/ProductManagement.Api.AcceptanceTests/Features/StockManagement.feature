Feature: Stock management
  As a store operator
  I want to add and remove stock for a product
  So that the catalogue always reflects what is physically available

  Background:
    Given a product "Progressive Lens Comfort" with 10 units in stock

  Scenario: Removing stock that is available
    When I remove 4 units from stock
    Then the request succeeds
    And the product has 6 units in stock

  Scenario: Removing more stock than is available is rejected
    When I remove 11 units from stock
    Then the request is rejected as a conflict with error code "product.insufficient_stock"
    And the product has 10 units in stock

  Scenario: Receiving new stock
    When I add 15 units to stock
    Then the request succeeds
    And the product has 25 units in stock

  Scenario Outline: Quantities must be positive
    When I remove <quantity> units from stock
    Then the request is rejected as invalid
    And the product has 10 units in stock

    Examples:
      | quantity |
      | 0        |
      | -3       |

  Scenario: Simultaneous orders never oversell
    When 30 customers each order 1 unit at the same time
    Then 10 orders succeed and 20 are rejected
    And the product has 0 units in stock

  Scenario Outline: Changing the stock of an unknown product is reported as not found
    When I <change> the stock of product 999999
    Then the request is rejected because the product does not exist
    And the product has 10 units in stock

    Examples:
      | change             |
      | remove 1 unit from |
      | add 1 unit to      |
