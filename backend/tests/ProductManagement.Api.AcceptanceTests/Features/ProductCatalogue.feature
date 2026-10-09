Feature: Product catalogue
  As a store operator
  I want to maintain and find products
  So that I can answer customer questions and keep the catalogue accurate

  Scenario: Creating a product assigns a unique six-digit identifier
    When I create a product named "Titanium Frame" priced 179.00 with 5 units in stock
    Then the product is created with a six-digit identifier
    And the product has 5 units in stock

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
