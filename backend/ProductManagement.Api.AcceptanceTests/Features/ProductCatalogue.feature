Feature: Product catalogue
  As a store operator
  I want to maintain and find products
  So that I can answer customer questions and keep the catalogue accurate

  Scenario: Creating a product assigns a unique six-digit identifier
    When I create a product named "Titanium Frame" priced 179.00 with 5 units in stock
    Then the product is created with a six-digit identifier
    And the product has 5 units in stock

  Scenario: Creating a product with missing or invalid details is rejected, naming each problem
    When I create a product with these details:
      | Field         | Value  |
      | name          |        |
      | price         | 10.999 |
      | stockQuantity | -1     |
    Then the request is rejected as invalid
    And each problem is named:
      | Field         | Problem                                                         |
      | name          | 'Name' must not be empty.                                       |
      | price         | 'Price' must not have more than 2 decimal places.               |
      | stockQuantity | 'Stock Quantity' must be between 0 and 1000000. You entered -1. |
      | categoryId    | 'Category Id' must not be empty.                                |

  Scenario: Browsing the catalogue shows each product's stock
    Given the following products exist:
      | Name             | Stock |
      | Titanium Frame   | 2     |
      | Cleaning Cloth   | 0     |
      | Progressive Lens | 5     |
    When I browse the catalogue
    Then I see these products:
      | Name             | Stock |
      | Cleaning Cloth   | 0     |
      | Progressive Lens | 5     |
      | Titanium Frame   | 2     |

  Scenario: Finding products by part of their name
    Given the following products exist:
      | Name                   | Stock |
      | Progressive Lens       | 5     |
      | Single Vision Lens 1.5 | 30    |
      | Titanium Frame         | 2     |
    When I search for "lens"
    Then I see these products:
      | Name                   | Stock |
      | Progressive Lens       | 5     |
      | Single Vision Lens 1.5 | 30    |

  Scenario: Finding products that are running low
    Given the following products exist:
      | Name              | Stock |
      | Cleaning Cloth    | 0     |
      | Sports Strap      | 3     |
      | Glasses Case      | 40    |
    When I look for products with between 0 and 5 units in stock
    Then I see these products:
      | Name           | Stock |
      | Cleaning Cloth | 0     |
      | Sports Strap   | 3     |

  Scenario: Saving changes to a product someone else has changed is rejected
    Given a product "Aviator Sunglasses" with 10 units in stock
    And I have opened the product for editing
    When someone else removes 1 unit from stock
    And I save my changes to the product
    Then the request is rejected because the product was modified in the meantime
    And the product has 9 units in stock

  Scenario: Updating a product's details
    Given a product "Aviator Sunglasses" with 10 units in stock
    When I rename the product to "Aviator Sunglasses Polarized" and change its price to 149.00
    Then the request succeeds
    And the product is named "Aviator Sunglasses Polarized" and priced 149.00
    And the product has 10 units in stock

  Scenario: Deleting a product
    Given a product "Sports Strap" with 3 units in stock
    When I delete the product
    Then the product is deleted
    And the product can no longer be found
